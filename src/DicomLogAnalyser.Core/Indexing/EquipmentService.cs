using System.Text.RegularExpressions;

namespace DicomLogAnalyser.Core.Indexing;

/// <summary>A piece of equipment named in the logs: its AE title and the modalities logged with it.</summary>
public sealed record Equipment(string Aet, IReadOnlyDictionary<string, long> Modalities, long Lines, IReadOnlyList<string> Addresses, long FirstTs, long LastTs);

/// <summary>
/// Lists the equipment (modalities) the logs name, from lines that state the modality and the AE title together, such as
/// "Mod US, PHILLIS4 192.0.2.10:51081 -> GATEWAYAE". Only DICOM modality codes are accepted, so other "Mod" words do
/// not create entries. Recomputed only when the index changes.
/// </summary>
public sealed partial class EquipmentService(IndexDatabase db)
{
    /// <summary>DICOM modality codes (Modality, 0008,0060), defined terms in current use.</summary>
    public static readonly IReadOnlySet<string> ModalityCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "AR", "ASMT", "AU", "BDUS", "BI", "BMD", "CR", "CT", "CTPROTOCOL", "DG", "DOC", "DX", "ECG", "EPS", "ES", "FID", "GM", "HC", "HD", "IO", "IOL",
        "IVOCT", "IVUS", "KER", "KO", "LEN", "LS", "MG", "MR", "M3D", "NM", "OAM", "OCT", "OP", "OPM", "OPT", "OPTBSV", "OPTENF", "OPV", "OSS", "OT", "PLAN",
        "PR", "PT", "PX", "REG", "RESP", "RF", "RG", "RTDOSE", "RTIMAGE", "RTINTENT", "RTPLAN", "RTRAD", "RTRECORD", "RTSEGANN", "RTSTRUCT", "RWV", "SEG",
        "SM", "SMR", "SR", "SRF", "STAIN", "TEXTUREMAP", "TG", "US", "VA", "XA", "XC",
    };

    public const int MaxAetLength = 16;

    [GeneratedRegex(@"\bMod(?:ality)?\s*[:=]?\s*(?<mod>[A-Z][A-Z0-9]{1,9})\s*[,;]\s*(?<aet>[A-Za-z0-9_\-.]{1,16})(?![A-Za-z0-9_\-.])(?:\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3}))?", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ModAet();

    private readonly Lock _lock = new();
    private (long Version, IReadOnlyList<Equipment> Value)? _cache;

    public IReadOnlyList<Equipment> List()
    {
        var version = db.DataVersion;
        lock (_lock)
        {
            if (_cache is { } cached && cached.Version == version) return cached.Value;
        }

        var value = Compute();
        lock (_lock) _cache = (version, value);
        return value;
    }

    /// <summary>The (modality, AE title, address) a line states, or null.</summary>
    public static (string Modality, string Aet, string? Address)? Parse(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        try
        {
            foreach (Match m in ModAet().Matches(message))
            {
                var mod = m.Groups["mod"].Value;
                if (!ModalityCodes.Contains(mod)) continue;
                var aet = m.Groups["aet"].Value;
                if (!aet.Any(char.IsLetter)) continue;
                return (mod, aet, m.Groups["ip"].Success ? m.Groups["ip"].Value : null);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological text: ignored.
        }

        return null;
    }

    private List<Equipment> Compute()
    {
        var found = new Dictionary<string, (Dictionary<string, long> Mods, long Lines, SortedSet<string> Ips, long First, long Last)>(StringComparer.Ordinal);
        using var c = db.Open();
        using var snapshot = ReadSnapshot.Begin(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT substr(message,1,1000), ifnull(ts, sort_ts) FROM events WHERE kind=0 AND (message LIKE '%Mod %' OR message LIKE '%Mod:%' OR message LIKE '%Mod=%' OR message LIKE '%Modality%')";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (Parse(r.GetString(0)) is not { } hit) continue;
            var ts = r.GetInt64(1);
            if (!found.TryGetValue(hit.Aet, out var e)) e = ([], 0, new SortedSet<string>(StringComparer.Ordinal), ts, ts);
            e.Mods[hit.Modality] = e.Mods.TryGetValue(hit.Modality, out var n) ? n + 1 : 1;
            if (hit.Address is { } ip && e.Ips.Count < 5) e.Ips.Add(ip);
            found[hit.Aet] = (e.Mods, e.Lines + 1, e.Ips, Math.Min(e.First, ts), Math.Max(e.Last, ts));
        }

        return found.OrderBy(kv => kv.Value.Mods.OrderByDescending(m => m.Value).First().Key, StringComparer.Ordinal).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new Equipment(kv.Key, kv.Value.Mods, kv.Value.Lines, kv.Value.Ips.ToList(), kv.Value.First, kv.Value.Last)).ToList();
    }

    /// <summary>Checks an AE title given by a user: 1 to 16 printable characters, no backslash, comma or double quote.</summary>
    public static bool IsValidAet(string? aet) =>
        aet is { Length: > 0 and <= MaxAetLength } && aet.Trim().Length == aet.Length && aet.All(ch => ch >= ' ' && ch < 127 && ch != '\\' && ch != ',' && ch != '"');

    /// <summary>Whether a message names one of the AE titles as a whole word (case-insensitive).</summary>
    public static Regex AetMatcher(IReadOnlyCollection<string> aets) =>
        new("(?<![A-Za-z0-9_\\-.])(?:" + string.Join('|', aets.Select(Regex.Escape)) + ")(?![A-Za-z0-9_\\-.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
}

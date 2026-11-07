using System.Globalization;
using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Parsing;
using Microsoft.Data.Sqlite;

namespace DicomLogAnalyser.Core.Worklist;

/// <summary>One DICOM association handled by the worklist provider (one connection from one piece of equipment).</summary>
public sealed class WorklistAssociation
{
    public required long StartTs { get; init; }
    public required bool UtcKnown { get; init; }
    public required long EventId { get; init; }
    public required string SourceLabel { get; init; }
    public required string Provider { get; init; }
    public string? CallingAe { get; set; }
    public string? CalledAe { get; set; }
    public string? Address { get; set; }
    public string? Implementation { get; set; }
    public int Echoes { get; set; }
    public int Queries { get; set; }
    public int Items { get; set; }
    public string? QueryModality { get; set; }
    public string? QueryStationAe { get; set; }
    public string? QueryDate { get; set; }
    public List<string> QueryOtherKeys { get; } = [];
    public string? WorklistFor { get; set; }
    public Dictionary<string, int> ItemStationAes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ItemModalities { get; } = new(StringComparer.Ordinal);
    public string? FinalStatus { get; set; }
    public string Outcome { get; set; } = "open";
    public bool HasDimseDetail { get; set; }

    /// <summary>Opened by the provider itself (for example its own query to the PACS), not by equipment.</summary>
    public bool Outbound { get; set; }
}

/// <summary>Everything seen from one calling AE title (one piece of equipment, or several sharing a title).</summary>
public sealed record WorklistEquipment(
    string CallingAe,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Implementations,
    IReadOnlyList<string> CalledAes,
    int Associations,
    int Echoes,
    int Queries,
    int Items,
    int EmptyAnswers,
    int Failures,
    IReadOnlyDictionary<string, int> QueryModalities,
    IReadOnlyDictionary<string, int> QueryStationAes,
    IReadOnlyDictionary<string, int> ItemStationAes,
    IReadOnlyDictionary<string, int> ItemModalities,
    IReadOnlyList<string> WorklistFor,
    long FirstTs,
    long LastTs,
    long LastEventId);

/// <summary>A possible configuration problem, with the AE title(s) it concerns.</summary>
public sealed record WorklistFinding(string Severity, string Code, string Title, string Detail, IReadOnlyList<string> AeTitles);

public sealed record WorklistReport(
    long? FromTs,
    long? ToTs,
    bool UtcKnown,
    int EventsRead,
    bool Truncated,
    bool DimseDetail,
    IReadOnlyList<WorklistEquipment> Equipment,
    IReadOnlyList<WorklistFinding> Findings,
    IReadOnlyList<WorklistAssociation> Recent,
    IReadOnlyList<string> Providers,
    IReadOnlyList<WorklistProvider> SupportedProviders);

/// <summary>
/// Reconstructs modality worklist traffic from the logs of a worklist provider (<see cref="WorklistProviders"/>): which equipment (calling AE title, address,
/// software) asks for the worklist, with which keys, and what is sent back, and points out AE title inconsistencies.
/// Lines are linked to an association by its number ("MR1(1910): ...") and, for lines without it, by the thread that
/// handles the association. Query keys and items are read from the DICOM dataset dumps written at DEBUG level.
/// </summary>
public sealed partial class WorklistAnalysis(IndexDatabase db)
{
    public const int MaxEvents = 400_000;
    public const int MaxRecent = 300;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    [GeneratedRegex(@"^(?<ae>[^\s(]{1,64})\((?<n>\d{1,9})\)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"A-ASSOCIATE-RQ (?<calling>\S{1,64}) >> (?<called>\S{1,64})", RegexOptions.CultureInvariant, 250)]
    private static partial Regex AssociateRq();

    [GeneratedRegex(@"accepted (?<ip>[0-9A-Fa-f.:\[\]]{2,64}?):(?<port>\d{1,5})\s*$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Accepted();

    [GeneratedRegex(@"C-FIND-RSP \[[^\]]*?status=(?<st>[0-9A-Fa-f]{1,4})H", RegexOptions.CultureInvariant, 250)]
    private static partial Regex FindRsp();

    [GeneratedRegex(@"^\s*(?<gt>>*)\((?<g>[0-9A-Fa-f]{4}),(?<e>[0-9A-Fa-f]{4})\) (?<vr>[A-Z]{2}) (?:#\d+ )?\[(?<val>.*)\](?: (?<name>[^\]]*))?$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex DatasetLine();

    [GeneratedRegex(@"^\s*implVersionName\s*=\s*(?<v>.*?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, 250)]
    private static partial Regex ImplVersion();

    [GeneratedRegex(@"^[\x20-\x5B\x5D-\x7E]{1,16}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ValidAe();

    public WorklistReport Analyse(IReadOnlyList<long>? sourceIds = null, int? hours = 24)
    {
        using var c = db.Open();
        using var snapshot = ReadSnapshot.Begin(c);
        var filter = sourceIds is { Count: > 0 } ? " AND e.source_id IN (" + string.Join(',', sourceIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")" : "";

        long? toTs = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT max(e.sort_ts) FROM events e JOIN files f ON f.id=e.file_id WHERE f.format IN (" + FormatList + ")" + filter;
            var v = cmd.ExecuteScalar();
            if (v is long l) toTs = l;
        }

        if (toTs is null) return new WorklistReport(null, null, true, 0, false, false, [], [], [], [], WorklistProviders.All);
        long? fromTs = hours is > 0 ? toTs.Value - (hours.Value * 3_600_000L) : null;

        var state = new ParseState();
        var events = 0;
        var truncated = false;
        var utcKnown = true;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.id, e.file_id, e.sort_ts, e.utc_known, e.category, e.component, e.message,
                       CASE WHEN e.message LIKE 'Dataset%' OR e.message LIKE 'A-ASSOCIATE-RQ%' THEN e.detail END, s.label, f.format
                FROM events e JOIN files f ON f.id=e.file_id JOIN sources s ON s.id=e.source_id
                WHERE f.format IN ({formats}) AND e.kind=0 AND e.category IN ('Association','PDUEncoder','PDUDecoder','DicomServiceRegistry','DicomWorklist')
                """.Replace("{formats}", FormatList, StringComparison.Ordinal) + filter + (fromTs is null ? "" : " AND e.sort_ts >= $from") + " ORDER BY e.file_id, e.line_start LIMIT $max";
            if (fromTs is not null) cmd.Parameters.AddWithValue("$from", fromTs.Value);
            cmd.Parameters.AddWithValue("$max", MaxEvents + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (++events > MaxEvents)
                {
                    truncated = true;
                    break;
                }

                if (r.GetInt64(3) == 0) utcKnown = false;
                state.Feed(new Row(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3) != 0, r.GetString(4),
                    r.IsDBNull(5) ? "" : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8),
                    WorklistProviders.ForFormat(r.IsDBNull(9) ? null : r.GetString(9))?.Name ?? "(unknown)"));
            }
        }

        var associations = state.All.Where(a => a.CallingAe is not null && !a.Outbound).ToList();
        var equipment = Summarise(associations);
        var findings = Findings(associations, equipment);
        var recent = associations.OrderByDescending(a => a.StartTs).Take(MaxRecent).ToList();
        var providers = associations.Select(a => a.Provider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new WorklistReport(fromTs, toTs, utcKnown, Math.Min(events, MaxEvents), truncated, associations.Any(a => a.HasDimseDetail), equipment, findings, recent,
            providers, WorklistProviders.All);
    }

    private sealed record Row(long Id, long FileId, long Ts, bool UtcKnown, string Logger, string Thread, string Message, string? Detail, string Source, string Provider);

    /// <summary>The log formats of all supported providers, as SQL string literals (fixed identifiers, not user input).</summary>
    private static readonly string FormatList = string.Join(',', WorklistProviders.All.Select(p => "'" + p.FormatId.Replace("'", "''", StringComparison.Ordinal) + "'"));

    /// <summary>Follows associations through the interleaved lines of one or more provider logs.</summary>
    private sealed class ParseState
    {
        private readonly Dictionary<(long File, string Number), WorklistAssociation> _byNumber = [];
        private readonly Dictionary<(long File, string Thread), WorklistAssociation> _byThread = [];
        private readonly Dictionary<WorklistAssociation, bool> _awaitingItem = [];

        public List<WorklistAssociation> All { get; } = [];

        public void Feed(Row row)
        {
            WorklistAssociation? a;
            var prefix = Prefix().Match(row.Message);
            if (prefix.Success)
            {
                var key = (row.FileId, prefix.Groups["n"].Value);
                var accepted = Accepted().Match(row.Message);
                var initiated = row.Message.Contains(") initiated ", StringComparison.Ordinal);
                if (accepted.Success || initiated || !_byNumber.TryGetValue(key, out a))
                {
                    // A new association: "accepted" opens it (numbers restart when the provider's service restarts); lines such
                    // as "close socket" after the release still belong to the same one.
                    a = new WorklistAssociation { StartTs = row.Ts, UtcKnown = row.UtcKnown, EventId = row.Id, SourceLabel = row.Source, Provider = row.Provider };
                    _byNumber[key] = a;
                    All.Add(a);
                }

                if (initiated) a.Outbound = true;

                if (accepted.Success)
                {
                    a.Address = accepted.Groups["ip"].Value;
                    return; // logged by the listener thread, not by the thread serving the association
                }

                _byThread[(row.FileId, row.Thread)] = a;
            }
            else if (!_byThread.TryGetValue((row.FileId, row.Thread), out a))
            {
                return;
            }

            Apply(a, row);
        }

        private void Apply(WorklistAssociation a, Row row)
        {
            var msg = row.Message;
            var rq = AssociateRq().Match(msg);
            if (rq.Success)
            {
                a.CallingAe = rq.Groups["calling"].Value;
                a.CalledAe = rq.Groups["called"].Value;
            }

            if (row.Detail is not null && msg.StartsWith("A-ASSOCIATE-RQ", StringComparison.Ordinal))
            {
                var impl = ImplVersion().Match(row.Detail);
                if (impl.Success && impl.Groups["v"].Value.Length > 0) a.Implementation = impl.Groups["v"].Value;
            }

            if (msg.Contains("A-ASSOCIATE-RJ", StringComparison.Ordinal)) a.Outcome = "rejected";
            else if (msg.Contains("A-ABORT", StringComparison.Ordinal) || msg.Contains("aborted", StringComparison.OrdinalIgnoreCase)) a.Outcome = "aborted";
            else if (msg.Contains("A-RELEASE-RP", StringComparison.Ordinal) && a.Outcome == "open") a.Outcome = "released";

            if (row.Logger == "PDUDecoder" && msg.Contains("C-FIND-RQ", StringComparison.Ordinal))
            {
                a.Queries++;
                a.HasDimseDetail = true;
            }

            if (row.Logger == "PDUDecoder" && msg.Contains("C-ECHO-RQ", StringComparison.Ordinal))
            {
                a.Echoes++;
                a.HasDimseDetail = true;
            }

            var rsp = row.Logger == "PDUEncoder" ? FindRsp().Match(msg) : Match.Empty;
            if (rsp.Success)
            {
                var status = rsp.Groups["st"].Value.ToUpperInvariant().TrimStart('0');
                if (status is "FF00" or "FF01")
                {
                    a.Items++;
                    _awaitingItem[a] = true;
                }
                else
                {
                    a.FinalStatus = status.Length == 0 ? "0000" : status.PadLeft(4, '0');
                }
            }

            if (row.Logger == "DicomWorklist" && msg.StartsWith("Loading data for ", StringComparison.Ordinal))
                a.WorklistFor = msg["Loading data for ".Length..].Trim();

            if (row.Detail is null || !msg.StartsWith("Dataset", StringComparison.Ordinal)) return;
            if (row.Logger == "DicomServiceRegistry") ReadQuery(a, row.Detail);
            else if (row.Logger == "PDUEncoder" && _awaitingItem.Remove(a)) ReadItem(a, row.Detail);
        }

        private static void ReadQuery(WorklistAssociation a, string detail)
        {
            foreach (var (tag, value, name) in Dataset(detail))
            {
                switch (tag)
                {
                    case "0008,0060": a.QueryModality ??= value; break;
                    case "0040,0001": a.QueryStationAe ??= value; break;
                    case "0040,0002": a.QueryDate ??= value; break;
                    default:
                        if (value.Length > 0 && tag is not "0008,0005" && !a.QueryOtherKeys.Contains(name)) a.QueryOtherKeys.Add(name);
                        break;
                }
            }

            a.QueryModality ??= "";
            a.QueryStationAe ??= "";
        }

        private static void ReadItem(WorklistAssociation a, string detail)
        {
            string? station = null, modality = null;
            foreach (var (tag, value, _) in Dataset(detail))
            {
                if (tag == "0040,0001") station ??= value;
                else if (tag == "0008,0060") modality ??= value;
            }

            Count(a.ItemStationAes, station is null ? "(absent)" : station.Length == 0 ? "(empty)" : station);
            Count(a.ItemModalities, modality is null ? "(absent)" : modality.Length == 0 ? "(empty)" : modality);
        }

        private static void Count(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;
    }

    /// <summary>Tag, value and attribute name of each line of a dataset dump (nesting is ignored).</summary>
    internal static IEnumerable<(string Tag, string Value, string Name)> Dataset(string detail)
    {
        foreach (var raw in detail.Split('\n'))
        {
            Match m;
            try
            {
                m = DatasetLine().Match(raw.TrimEnd('\r'));
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!m.Success) continue;
            var tag = (m.Groups["g"].Value + "," + m.Groups["e"].Value).ToUpperInvariant();
            yield return (tag, m.Groups["val"].Value.Trim(), m.Groups["name"].Value.Trim());
        }
    }

    private static List<WorklistEquipment> Summarise(List<WorklistAssociation> associations) =>
        associations.GroupBy(a => a.CallingAe!, StringComparer.Ordinal).Select(g =>
        {
            var list = g.OrderBy(a => a.StartTs).ToList();
            return new WorklistEquipment(
                g.Key,
                Distinct(list.Select(a => a.Address)),
                Distinct(list.Select(a => a.Implementation)),
                Distinct(list.Select(a => a.CalledAe)),
                list.Count,
                list.Sum(a => a.Echoes),
                list.Sum(a => a.Queries),
                list.Sum(a => a.Items),
                list.Count(a => a.Queries > 0 && a.Items == 0),
                list.Count(IsFailure),
                Tally(list.Where(a => a.Queries > 0).Select(a => Show(a.QueryModality))),
                Tally(list.Where(a => a.Queries > 0).Select(a => Show(a.QueryStationAe))),
                Merge(list.Select(a => a.ItemStationAes)),
                Merge(list.Select(a => a.ItemModalities)),
                Distinct(list.Select(a => a.WorklistFor)),
                list[0].StartTs,
                list[^1].StartTs,
                list[^1].EventId);
        }).OrderBy(e => e.CallingAe, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsFailure(WorklistAssociation a) =>
        a.Outcome is "rejected" or "aborted" || (a.FinalStatus is not null && a.FinalStatus != "0000");

    private static string Show(string? v) => string.IsNullOrEmpty(v) ? "(any)" : v;

    private static List<string> Distinct(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static Dictionary<string, int> Tally(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> Merge(IEnumerable<Dictionary<string, int>> parts)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in parts)
        {
            foreach (var (k, v) in p) d[k] = d.GetValueOrDefault(k) + v;
        }

        return d;
    }

    private static readonly string[] GenericAeTitles = ["ANY-SCP", "ANYSCP", "STORESCU", "STORESCP", "FINDSCU", "ECHOSCU", "DEFAULT", "MODALITY", "WORKLIST", "MWL", "DICOM", "TEST"];

    private static List<WorklistFinding> Findings(List<WorklistAssociation> associations, List<WorklistEquipment> equipment)
    {
        var f = new List<WorklistFinding>();
        foreach (var e in equipment)
        {
            var ae = e.CallingAe;
            if (e.Queries > 0 && e.Items == 0)
            {
                f.Add(new("warning", "never-items", $"{ae} never received a worklist item",
                    $"{e.Queries} queries, every answer was empty. The worklist side may not know this AE title, or nothing is scheduled for it. Query keys used: modality {Keys(e.QueryModalities)}, station AE {Keys(e.QueryStationAes)}.", [ae]));
            }

            var otherStations = e.ItemStationAes.Where(kv => kv.Key != ae).ToList();
            if (otherStations.Count > 0)
            {
                f.Add(new("warning", "item-station-ae", $"{ae} received items scheduled for another AE title",
                    "Items whose Scheduled Station AE Title differs from the calling AE: " + string.Join(", ", otherStations.Select(kv => $"{kv.Key} ({kv.Value})")) + ".", [ae, .. otherStations.Select(kv => kv.Key)]));
            }

            var otherFilters = e.QueryStationAes.Keys.Where(k => k != "(any)" && k != ae).ToList();
            if (otherFilters.Count > 0)
            {
                f.Add(new("warning", "query-station-ae", $"{ae} asks for another station's worklist",
                    "The query's Scheduled Station AE Title is " + string.Join(", ", otherFilters) + ", not the equipment's own AE title.", [ae, .. otherFilters]));
            }

            var otherLists = e.WorklistFor.Where(w => w != ae).ToList();
            if (otherLists.Count > 0)
            {
                f.Add(new("warning", "worklist-for", $"The worklist provider loaded another worklist for {ae}",
                    "Worklist loaded for: " + string.Join(", ", otherLists) + ".", [ae, .. otherLists]));
            }

            if (e.Addresses.Count > 1)
            {
                f.Add(new("warning", "ae-several-addresses", $"{ae} is used from {e.Addresses.Count} addresses",
                    "Addresses: " + string.Join(", ", e.Addresses) + ". Either several devices share this AE title, or the device changed address.", [ae]));
            }

            if (e.Implementations.Count > 1)
            {
                f.Add(new("info", "ae-several-implementations", $"{ae} reported different software",
                    "Implementation versions: " + string.Join(", ", e.Implementations) + ". A software update, or several devices sharing the title.", [ae]));
            }

            if (!ValidAe().IsMatch(ae) || ae != ae.Trim())
            {
                f.Add(new("warning", "ae-invalid", $"{ae} is not a valid DICOM AE title",
                    "AE titles have at most 16 characters, without backslash or control characters, and no leading or trailing spaces.", [ae]));
            }
            else if (GenericAeTitles.Contains(ae.ToUpperInvariant()) || ae.StartsWith("AE_", StringComparison.OrdinalIgnoreCase))
            {
                f.Add(new("info", "ae-generic", $"{ae} looks like a default AE title",
                    "Generic or factory titles are easily duplicated and seldom match the title configured on the worklist side.", [ae]));
            }

            var aeVendor = VendorFromTitle(ae);
            var swVendors = e.Implementations.Select(VendorFromSoftware).Where(v => v is not null).Distinct().ToList();
            if (aeVendor is not null && swVendors.Count > 0 && !swVendors.Contains(aeVendor))
            {
                f.Add(new("info", "ae-vendor", $"{ae} suggests {aeVendor}, but the software is {string.Join(", ", swVendors)}",
                    "Implementation: " + string.Join(", ", e.Implementations) + ". Check that the AE title belongs to this device.", [ae]));
            }

            if (e.Failures > 0)
            {
                f.Add(new("error", "failures", $"{ae}: {e.Failures} association(s) failed",
                    "Rejected or aborted associations, or a final status other than success.", [ae]));
            }

            var itemMods = e.ItemModalities.Keys.Where(m => m is not "(absent)" and not "(empty)").ToList();
            var queryMods = e.QueryModalities.Keys.Where(m => m != "(any)").ToList();
            var unexpected = itemMods.Where(m => queryMods.Count > 0 && !queryMods.Contains(m)).ToList();
            if (unexpected.Count > 0)
            {
                f.Add(new("info", "modality", $"{ae} received items of another modality",
                    $"Asked for {string.Join(", ", queryMods)}, received {string.Join(", ", unexpected)}.", [ae]));
            }
        }

        foreach (var g in associations.Where(a => a.Address is not null).GroupBy(a => a.Address!).Where(g => g.Select(a => a.CallingAe).Distinct().Count() > 1))
        {
            var titles = g.Select(a => a.CallingAe!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            f.Add(new("warning", "address-several-aes", $"Address {g.Key} uses {titles.Count} AE titles",
                "AE titles: " + string.Join(", ", titles) + ". One device configured with several titles, or devices behind one address.", titles));
        }

        var called = associations.Where(a => a.CalledAe is not null && a.CallingAe is not null && a.Queries + a.Echoes > 0).GroupBy(a => a.CalledAe!).OrderByDescending(g => g.Count()).ToList();
        if (called.Count > 1)
        {
            foreach (var g in called.Skip(1))
            {
                var titles = g.Select(a => a.CallingAe!).Distinct(StringComparer.Ordinal).ToList();
                f.Add(new("warning", "called-ae", $"Called AE {g.Key} differs from the usual {called[0].Key}",
                    $"Used by {string.Join(", ", titles)} in {g.Count()} association(s).", titles));
            }
        }

        if (associations.Count > 0 && !associations.Any(a => a.HasDimseDetail))
        {
            f.Add(new("info", "no-debug", "Query keys and items are not visible",
                "The worklist provider logs only at INFO level here. Its DEBUG level shows the C-FIND requests, the keys and every item sent.", []));
        }

        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        return f.OrderBy(x => order[x.Severity]).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Keys(IReadOnlyDictionary<string, int> d) => d.Count == 0 ? "(none)" : string.Join(", ", d.Keys);

    internal static string? VendorFromTitle(string ae)
    {
        var u = ae.ToUpperInvariant();
        if (u.StartsWith("PHIL", StringComparison.Ordinal)) return "Philips";
        if (u.StartsWith("SIE", StringComparison.Ordinal)) return "Siemens";
        if (u.StartsWith("GE", StringComparison.Ordinal) && !u.StartsWith("GEN", StringComparison.Ordinal)) return "GE";
        if (u.StartsWith("HOLO", StringComparison.Ordinal)) return "Hologic";
        if (u.StartsWith("FUJI", StringComparison.Ordinal)) return "Fujifilm";
        if (u.StartsWith("CANON", StringComparison.Ordinal) || u.StartsWith("TOSH", StringComparison.Ordinal)) return "Canon";
        return null;
    }

    internal static string? VendorFromSoftware(string impl)
    {
        var u = impl.ToUpperInvariant();
        if (u.Contains("SIEMENS", StringComparison.Ordinal) || u.Contains("SYNGO", StringComparison.Ordinal)) return "Siemens";
        if (u.Contains("LOGIQ", StringComparison.Ordinal) || u.Contains("VOLUSON", StringComparison.Ordinal) || u.StartsWith("GE_", StringComparison.Ordinal) || u.Contains("GEMS", StringComparison.Ordinal)) return "GE";
        if (u.Contains("PHILIPS", StringComparison.Ordinal) || u.Contains("EPIQ", StringComparison.Ordinal) || u.Contains("AFFINITI", StringComparison.Ordinal)) return "Philips";
        if (u.Contains("HOLOGIC", StringComparison.Ordinal)) return "Hologic";
        if (u.Contains("FUJI", StringComparison.Ordinal)) return "Fujifilm";
        if (u.Contains("CANON", StringComparison.Ordinal) || u.Contains("TOSHIBA", StringComparison.Ordinal) || u.Contains("APLIO", StringComparison.Ordinal)) return "Canon";
        return null;
    }
}

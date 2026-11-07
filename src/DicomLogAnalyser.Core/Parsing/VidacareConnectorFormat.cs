using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Parsing;

/// <summary>
/// VIDAcare Connector logs (log4net layout, fo-dicom network messages), usually in <c>VIDAcare\Connector\logs</c>, e.g.
/// <c>2026-11-05 15:39:42,538 [RIS:12] INFO  Association - Association(1910) accepted 192.0.2.10:53598 | 6 907 552</c>.
/// The thread name is kept as the component and the logger name as the category; the trailing memory counter
/// (<c>| 6 907 552</c>) is removed. DICOM dataset dumps and association details are continuation lines. The timestamp
/// has no UTC offset, so the source's time zone applies.
/// </summary>
public sealed partial class VidacareConnectorFormat : ILogFormat
{
    public const string FormatId = "vidacare-connector";

    public string Id => FormatId;

    // Provider-neutral wording: the provider appears only as a value (see Worklist.WorklistProviders).
    public string DisplayName => "Worklist provider log (provider: VIDAcare)";

    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2}) (?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2}),(?<f>\d{3}) \[(?<thr>[^\]\r\n]{1,100})\] (?<lvl>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL) +(?<cat>[A-Za-z_][\w.`]{0,120}) - (?<msg>.*?)(?: \|(?: [0-9  ]+)+)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Header();

    public RecordHeader? TryParseHeader(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length < 30 || line.Length > 16 * 1024 || !char.IsAsciiDigit(line[0])) return null;
        Match m;
        try
        {
            m = Header().Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!m.Success) return null;
        var levelText = m.Groups["lvl"].Value;
        var level = B3dWebGatewayFormat.MapLevel(levelText);
        if (level is null || !FormatHelpers.TryBuildDate(m, out var timestamp)) return null;
        return new RecordHeader(level.Value, levelText, timestamp, null, m.Groups["cat"].Value, m.Groups["thr"].Value, m.Groups["msg"].Value);
    }
}

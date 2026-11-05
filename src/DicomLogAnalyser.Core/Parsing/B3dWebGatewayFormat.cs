using System.Globalization;
using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Parsing;

/// <summary>
/// B3DWEB Gateway logs, e.g. <c>INF 2026.11.03 09:40:29 SPS - message</c> or <c>WARNING 2026.11.03 05:00:17 SML - message</c>.
/// Timestamps carry no time-zone information: they are the gateway's local wall-clock time.
/// </summary>
public sealed partial class B3dWebGatewayFormat : ILogFormat
{
    public const string FormatId = "b3dweb-gateway";

    public string Id => FormatId;

    public string DisplayName => "B3DWEB Gateway";

    [GeneratedRegex(@"^(?<lvl>[A-Z]{3,8})[ \t]+(?<y>\d{4})\.(?<mo>\d{2})\.(?<d>\d{2})[ \t]+(?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?:[.,](?<f>\d{1,7}))?[ \t]+(?<cat>[A-Z][A-Z0-9_]{1,11})[ \t]+-(?:[ \t](?<msg>.*))?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Header();

    public RecordHeader? TryParseHeader(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length < 24 || line.Length > 16 * 1024 || !char.IsAsciiLetterUpper(line[0])) return null;
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
        var level = MapLevel(levelText);
        if (level is null) return null;
        if (!FormatHelpers.TryBuildDate(m, out var timestamp)) return null;
        return new RecordHeader(level.Value, levelText, timestamp, null, m.Groups["cat"].Value, null, m.Groups["msg"].Value);
    }

    internal static EventLevel? MapLevel(string text) => text switch
    {
        "TRC" or "TRACE" or "VRB" or "VERBOSE" => EventLevel.Trace,
        "DBG" or "DEBUG" => EventLevel.Debug,
        "INF" or "INFO" or "INFORMATION" => EventLevel.Info,
        "WRN" or "WARN" or "WARNING" => EventLevel.Warning,
        "ERR" or "ERROR" => EventLevel.Error,
        "FTL" or "FATAL" or "CRITICAL" or "CRT" => EventLevel.Fatal,
        _ => null,
    };
}

internal static class FormatHelpers
{
    public static bool TryBuildDate(Match m, out DateTime value)
    {
        value = default;
        static int I(Match m, string g) => int.Parse(m.Groups[g].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        int y = I(m, "y"), mo = I(m, "mo"), d = I(m, "d"), h = I(m, "h"), mi = I(m, "mi"), s = I(m, "s");
        if (y < 1990 || y > 2200 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59) return false;
        var ticks = 0L;
        var f = m.Groups["f"];
        if (f.Success)
        {
            var digits = f.Value.PadRight(7, '0');
            ticks = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        value = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified).AddTicks(ticks);
        return true;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Parsing;

/// <summary>
/// DICOM Router logs (Serilog-style), e.g.
/// <c>2026-10-27 00:00:06.059 +00:00 [INF] DicomRouter.Dicom.RouterScp: message</c>.
/// The timestamp carries its UTC offset, so events can be placed on a common UTC timeline.
/// </summary>
public sealed partial class DicomRouterFormat : ILogFormat
{
    public const string FormatId = "dicom-router";

    public string Id => FormatId;

    public string DisplayName => "DICOM Router";

    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})[ T](?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?:[.,](?<f>\d{1,7}))?[ \t]*(?<off>Z|[+-]\d{2}:?\d{2})?[ \t]+\[(?<lvl>[A-Za-z]{3,11})\][ \t]*(?:(?<comp>[A-Za-z_][\w`]*(?:\.[A-Za-z_][\w`]*)+(?:\[[^\]\r\n]{0,200}\])?):(?:[ \t]|$))?(?<msg>.*)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Header();

    public RecordHeader? TryParseHeader(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length < 24 || line.Length > 16 * 1024 || !char.IsAsciiDigit(line[0])) return null;
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
        var levelText = m.Groups["lvl"].Value.ToUpperInvariant();
        var level = B3dWebGatewayFormat.MapLevel(levelText);
        if (level is null) return null;
        if (!FormatHelpers.TryBuildDate(m, out var timestamp)) return null;
        TimeSpan? offset = null;
        var off = m.Groups["off"];
        if (off.Success)
        {
            if (off.Value == "Z")
            {
                offset = TimeSpan.Zero;
            }
            else
            {
                var digits = off.Value.Replace(":", "", StringComparison.Ordinal);
                var hours = int.Parse(digits.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture);
                var minutes = int.Parse(digits.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture);
                if (hours > 14 || minutes > 59) return null;
                offset = new TimeSpan(hours, minutes, 0) * (digits[0] == '-' ? -1 : 1);
            }
        }

        var component = m.Groups["comp"].Success ? m.Groups["comp"].Value : null;
        return new RecordHeader(level.Value, levelText, timestamp, offset, CategoryOf(component), component, m.Groups["msg"].Value);
    }

    /// <summary>Short, human-friendly category: the last segment of the component, without generic arguments.</summary>
    internal static string CategoryOf(string? component)
    {
        if (string.IsNullOrEmpty(component)) return "(none)";
        var c = component;
        var bracket = c.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0) c = c[..bracket];
        var tick = c.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0) c = c[..tick];
        var dot = c.LastIndexOf('.');
        return dot >= 0 && dot < c.Length - 1 ? c[(dot + 1)..] : c;
    }
}

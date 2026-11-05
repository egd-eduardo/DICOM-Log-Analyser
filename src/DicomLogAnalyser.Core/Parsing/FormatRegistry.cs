namespace DicomLogAnalyser.Core.Parsing;

/// <summary>Known formats and content-based format detection.</summary>
public static class FormatRegistry
{
    public const string Auto = "auto";

    public static IReadOnlyList<ILogFormat> All { get; } = [new B3dWebGatewayFormat(), new DicomRouterFormat(), new VidacareConnectorFormat()];

    public static ILogFormat? Find(string? id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    public static bool IsValidHint(string? hint) => hint is null || hint == Auto || Find(hint) is not null;

    /// <summary>
    /// Chooses the format whose header pattern matches most of the sample lines. Returns null when no line of the
    /// sample is recognised (the file is reported as unrecognised instead of being indexed as noise).
    /// </summary>
    public static ILogFormat? Detect(IEnumerable<string> sampleLines)
    {
        ArgumentNullException.ThrowIfNull(sampleLines);
        var counts = new int[All.Count];
        foreach (var line in sampleLines)
        {
            for (var i = 0; i < All.Count; i++)
            {
                if (All[i].TryParseHeader(line) is not null) counts[i]++;
            }
        }

        var best = -1;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0 && (best < 0 || counts[i] > counts[best])) best = i;
        }

        return best < 0 ? null : All[best];
    }
}

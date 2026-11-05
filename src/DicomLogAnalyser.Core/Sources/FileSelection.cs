namespace DicomLogAnalyser.Core.Sources;

public sealed record CandidateFile(string RelPath, long Size, DateTimeOffset ModifiedUtc, DateOnly ModifiedDate);

/// <summary>
/// Limit on how much of a folder is analysed: the chosen files' last-modified dates must lie within
/// <see cref="MaxDays"/> consecutive calendar days, counted in the source's time zone (or this computer's when none is
/// set). The chosen files are indexed completely. Imported bundles are not limited.
/// </summary>
public static class FileSelection
{
    public const int MaxDays = 5;
    public const int MaxListed = 5000;

    public static IReadOnlyList<CandidateFile> List(SourceDefinition source, TimeZoneInfo? zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tz = zone ?? TimeZoneInfo.Local;
        return new FileEnumerator(source.Path, source.Recursive, PathValidator.SplitPatterns(source.IncludePatterns)).Enumerate()
            .Select(f => Candidate(source.Path, f, tz))
            .OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.RelPath, StringComparer.Ordinal)
            .ToList();
    }

    public static CandidateFile Candidate(string root, FileInfo file, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(tz);
        var utc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        return new CandidateFile(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length, utc, DateOnly.FromDateTime(local.DateTime));
    }

    /// <summary>Number of calendar days covered by the files' modification dates (0 for none).</summary>
    public static int SpanDays(IEnumerable<CandidateFile> files)
    {
        var dates = files.Select(f => f.ModifiedDate).ToList();
        return dates.Count == 0 ? 0 : dates.Max().DayNumber - dates.Min().DayNumber + 1;
    }

    /// <summary>The files modified during the most recent <see cref="MaxDays"/> calendar days of the folder.</summary>
    public static IReadOnlyList<string> Default(IReadOnlyList<CandidateFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return [];
        var newest = files.Max(f => f.ModifiedDate);
        var first = newest.AddDays(-(MaxDays - 1));
        return files.Where(f => f.ModifiedDate >= first).Select(f => f.RelPath).ToList();
    }

    /// <summary>Returns a problem with a requested selection, or null when it is acceptable.</summary>
    public static string? Validate(IReadOnlyList<string>? selected, IReadOnlyList<CandidateFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (selected is null || selected.Count == 0) return "Choose at least one file.";
        if (selected.Count > MaxListed) return "Too many files selected.";
        var byPath = files.ToDictionary(f => f.RelPath, StringComparer.Ordinal);
        var chosen = new List<CandidateFile>();
        foreach (var path in selected.Distinct(StringComparer.Ordinal))
        {
            if (!byPath.TryGetValue(path, out var f)) return "A selected file is no longer in the folder. Reload the file list and choose again.";
            chosen.Add(f);
        }

        var span = SpanDays(chosen);
        return span > MaxDays
            ? $"The selected files were modified over {span} calendar days. At most {MaxDays} days can be analysed per folder: choose files from {MaxDays} consecutive days or fewer."
            : null;
    }
}

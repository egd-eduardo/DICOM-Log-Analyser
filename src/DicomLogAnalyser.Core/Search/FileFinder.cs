using System.Buffers;
using System.Diagnostics;
using DicomLogAnalyser.Core.IO;

namespace DicomLogAnalyser.Core.Search;

/// <summary>A file to search, already resolved and checked by the caller.</summary>
public sealed record FindTarget(long SourceId, string SourceLabel, string RelPath, string FullPath);

/// <summary>One matching line with the lines around it.</summary>
public sealed record FindHit(long SourceId, string SourceLabel, string File, long Line, string Text, IReadOnlyList<string> Before, IReadOnlyList<string> After);

public sealed record FindResult(
    IReadOnlyList<FindHit> Hits,
    int FilesSearched,
    long BytesRead,
    bool Truncated,
    bool TimedOut,
    double Seconds,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> Terms);

/// <summary>
/// Searches files directly, line by line, without the index, like "Find in Files" in a text editor: results are
/// available at once, also for files that are not (yet) indexed. Case- and accent-insensitive. All words must appear on
/// the line; "quotes" keep a phrase with spaces; a leading - excludes lines containing the word. Files are only read.
/// </summary>
public static class FileFinder
{
    public const int DefaultMaxHits = 500;
    public const int ContextLines = 2;
    public const int MaxLineChars = 4000;

    /// <summary>Parses the search text into folded terms to include and to exclude.</summary>
    public static (IReadOnlyList<string> Include, IReadOnlyList<string> Exclude) Parse(string? text)
    {
        var include = new List<string>();
        var exclude = new List<string>();
        var s = text ?? "";
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            var negative = s[i] == '-' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]);
            if (negative) i++;
            string term;
            if (s[i] == '"')
            {
                var end = s.IndexOf('"', i + 1);
                term = end < 0 ? s[(i + 1)..] : s[(i + 1)..end];
                i = end < 0 ? s.Length : end + 1;
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                term = s[start..i];
            }

            if (term.Length == 0) continue;
            (negative ? exclude : include).Add(TextFolding.Fold(term));
        }

        return (include, exclude);
    }

    public static FindResult Find(IReadOnlyList<FindTarget> targets, string text, int maxHits, TimeSpan timeLimit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var (include, exclude) = Parse(text);
        var clock = Stopwatch.StartNew();
        var hits = new List<FindHit>();
        var unreadable = new List<string>();
        long bytes = 0;
        var files = 0;
        var truncated = false;
        var timedOut = false;
        if (include.Count == 0) return new FindResult(hits, 0, 0, false, false, 0, unreadable, include);

        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            foreach (var target in targets)
            {
                if (truncated || timedOut) break;
                ct.ThrowIfCancellationRequested();
                FileStream stream;
                try
                {
                    stream = LineReader.OpenReadOnly(target.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(target.SourceLabel + ": " + target.RelPath);
                    continue;
                }

                files++;
                using (stream)
                using (var reader = new LineReader(stream, 0, 1))
                {
                    var before = new Queue<string>(ContextLines);
                    var open = new List<(FindHit Hit, List<string> After)>();
                    var count = 0;
                    while (reader.ReadLine() is { } line)
                    {
                        var shown = line.Text.Length > MaxLineChars ? line.Text[..MaxLineChars] + "…" : line.Text;
                        for (var o = open.Count - 1; o >= 0; o--)
                        {
                            open[o].After.Add(shown);
                            if (open[o].After.Count >= ContextLines) open.RemoveAt(o);
                        }

                        // Once the cap is reached, read on only to complete the lines after the last hits.
                        if (truncated)
                        {
                            if (open.Count == 0) break;
                            continue;
                        }

                        if (Matches(line.Text, include, exclude, ref buffer))
                        {
                            if (hits.Count >= maxHits)
                            {
                                truncated = true;
                                if (open.Count == 0) break;
                                continue;
                            }

                            var after = new List<string>(ContextLines);
                            var hit = new FindHit(target.SourceId, target.SourceLabel, target.RelPath, line.LineNumber, shown, before.ToArray(), after);
                            hits.Add(hit);
                            open.Add((hit, after));
                        }

                        before.Enqueue(shown);
                        if (before.Count > ContextLines) before.Dequeue();
                        if ((++count & 0x3FF) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (clock.Elapsed > timeLimit)
                            {
                                timedOut = true;
                                break;
                            }
                        }
                    }

                    bytes += reader.Position;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        return new FindResult(hits, files, bytes, truncated, timedOut, clock.Elapsed.TotalSeconds, unreadable, include);
    }

    private static bool Matches(string line, IReadOnlyList<string> include, IReadOnlyList<string> exclude, ref char[] buffer)
    {
        if (buffer.Length < line.Length)
        {
            ArrayPool<char>.Shared.Return(buffer);
            buffer = ArrayPool<char>.Shared.Rent(line.Length);
        }

        var folded = buffer.AsSpan(0, line.Length);
        TextFolding.Fold(line, folded);
        foreach (var term in include)
        {
            if (folded.IndexOf(term, StringComparison.Ordinal) < 0) return false;
        }

        foreach (var term in exclude)
        {
            if (folded.IndexOf(term, StringComparison.Ordinal) >= 0) return false;
        }

        return true;
    }
}

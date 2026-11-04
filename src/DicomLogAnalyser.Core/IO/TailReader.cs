using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Parsing;

namespace DicomLogAnalyser.Core.IO;

public sealed record TailLine(string Text, EventLevel? Level);

/// <summary>Result of one live read. <see cref="Offset"/> is where the next read continues (end of the last complete line).</summary>
public sealed record TailChunk(IReadOnlyList<TailLine> Lines, long Offset, long Size, bool Reset, bool HeadTruncated, DateTimeOffset ModifiedUtc);

/// <summary>
/// Live view of a log file that is still being written (in the style of BareTail). Reads strictly read-only with
/// sharing, never holds the file open between calls, returns complete lines only (a partly written last line is
/// delivered once its line break arrives) and detects truncation or replacement. Text is UTF-8 with a per-line
/// Latin-1 fallback, as in indexing.
/// </summary>
public static class TailReader
{
    public const int MaxLines = 5000;
    public const int MaxBytesPerRead = 2 * 1024 * 1024;

    /// <summary>Last <paramref name="lines"/> complete lines of the file.</summary>
    public static TailChunk ReadLast(string path, int lines)
    {
        lines = Math.Clamp(lines, 1, MaxLines);
        using var s = LineReader.OpenReadOnly(path);
        var size = s.Length;
        var start = Math.Max(0, size - MaxBytesPerRead);
        var buffer = ReadRange(s, start, size - start);
        var end = LastLineBreak(buffer);
        if (end < 0) return new TailChunk([], start, size, false, start > 0, Modified(path));
        // Skip the first, possibly incomplete, line when not starting at the beginning of the file.
        var from = 0;
        if (start > 0)
        {
            var first = Array.IndexOf(buffer, (byte)'\n');
            from = first < 0 ? end + 1 : first + 1;
        }

        var all = Split(buffer, from, end + 1, start == 0);
        var headTruncated = start > 0 || all.Count > lines;
        var result = all.Count > lines ? all.GetRange(all.Count - lines, lines) : all;
        return new TailChunk(Classify(result), start + end + 1, size, false, headTruncated, Modified(path));
    }

    /// <summary>
    /// Complete lines written since <paramref name="offset"/>. When the file became shorter than the offset (truncated,
    /// rotated or replaced), the chunk is marked <see cref="TailChunk.Reset"/> and contains the file's last lines.
    /// </summary>
    public static TailChunk ReadFrom(string path, long offset, int resetLines)
    {
        using (var probe = LineReader.OpenReadOnly(path))
        {
            if (offset < 0 || probe.Length < offset) return ReadLast(path, resetLines) with { Reset = true };
            var size = probe.Length;
            if (size == offset) return new TailChunk([], offset, size, false, false, Modified(path));
            var length = Math.Min(size - offset, MaxBytesPerRead);
            var buffer = ReadRange(probe, offset, length);
            var end = LastLineBreak(buffer);
            if (end < 0)
            {
                // No complete line yet. A single line longer than the read window is delivered in pieces.
                return length == MaxBytesPerRead
                    ? new TailChunk(Classify(Split(buffer, 0, buffer.Length, offset == 0)), offset + length, size, false, false, Modified(path))
                    : new TailChunk([], offset, size, false, false, Modified(path));
            }

            return new TailChunk(Classify(Split(buffer, 0, end + 1, offset == 0)), offset + end + 1, size, false, false, Modified(path));
        }
    }

    private static byte[] ReadRange(Stream s, long start, long length)
    {
        s.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[length];
        var read = s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static int LastLineBreak(byte[] buffer) => Array.LastIndexOf(buffer, (byte)'\n');

    private static List<string> Split(byte[] buffer, int from, int to, bool atFileStart)
    {
        var lines = new List<string>();
        var pos = from;
        while (pos < to)
        {
            var nl = Array.IndexOf(buffer, (byte)'\n', pos, to - pos);
            var stop = nl < 0 ? to : nl;
            var span = buffer.AsSpan(pos, stop - pos);
            if (span.Length > 0 && span[^1] == '\r') span = span[..^1];
            if (atFileStart && pos == 0 && span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) span = span[3..];
            if (span.Length > LineReader.MaxLineBytes) span = span[..LineReader.MaxLineBytes];
            lines.Add(LineReader.Decode(span));
            pos = stop + 1;
        }

        return lines;
    }

    /// <summary>Level of record header lines; continuation lines have no level (the viewer keeps the previous one).</summary>
    private static List<TailLine> Classify(List<string> lines) =>
        lines.Select(l => new TailLine(l, FormatRegistry.All.Select(f => f.TryParseHeader(l)?.Level).FirstOrDefault(v => v is not null))).ToList();

    private static DateTimeOffset Modified(string path) => new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
}

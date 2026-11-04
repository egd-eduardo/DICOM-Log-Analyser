using System.Text;
using DicomLogAnalyser.Core.IO;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Parsing;

/// <summary>
/// Groups physical lines into normalised events. A header line recognised by the format starts a record; following
/// lines that are not headers are continuation lines of that record (multi-line messages and .NET exception traces).
/// Lines that cannot be attached to a record (before the first header, or beyond the continuation limits) become
/// <see cref="EventKind.Unparsed"/> blocks so nothing is silently dropped.
/// </summary>
public sealed class RecordAssembler
{
    public const int MaxContinuationLines = 2000;
    public const int MaxDetailChars = 256 * 1024;
    public const int MaxUnparsedBlockLines = 200;
    public const int MaxMessageChars = 16 * 1024;

    private readonly ILogFormat _format;
    private readonly TimeZoneInfo? _timeZone;
    private LogEvent? _current;
    private StringBuilder? _detail;
    private int _continuationLines;
    private bool _currentFull;

    public RecordAssembler(ILogFormat format, TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(format);
        _format = format;
        _timeZone = timeZone;
    }

    /// <summary>Number of physical lines that were not part of any recognised record.</summary>
    public long UnparsedLines { get; private set; }

    /// <summary>The record still being assembled (more continuation lines may follow).</summary>
    public LogEvent? Pending => _current;

    /// <summary>Adds a line; returns the previously open event when this line closes it, otherwise null.</summary>
    public LogEvent? Add(SourceLine line)
    {
        var header = _format.TryParseHeader(line.Text);
        if (header is not null)
        {
            var finished = Close();
            _current = NewRecord(line, header);
            return finished;
        }

        if (_current is not null && _current.Kind == EventKind.Record && !_currentFull)
        {
            if (_continuationLines < MaxContinuationLines && (_detail?.Length ?? 0) + line.Text.Length <= MaxDetailChars)
            {
                AppendDetail(line);
                if (LooksLikeException(line.Text)) _current.HasException = true;
                return null;
            }

            _currentFull = true;
        }

        UnparsedLines++;
        if (_current is not null && _current.Kind == EventKind.Unparsed && _continuationLines < MaxUnparsedBlockLines)
        {
            AppendDetail(line);
            return null;
        }

        var closed = Close();
        _current = new LogEvent
        {
            Kind = EventKind.Unparsed,
            Level = EventLevel.Unknown,
            LineStart = line.LineNumber,
            LineEnd = line.LineNumber,
            ByteStart = line.ByteStart,
            ByteEnd = line.ByteEnd,
            Category = "(unparsed)",
            Message = Truncate(line.Text, MaxMessageChars),
        };
        return closed;
    }

    /// <summary>Closes and returns the open event, if any.</summary>
    public LogEvent? Close()
    {
        var e = _current;
        if (e is null) return null;
        if (_detail is { Length: > 0 })
        {
            var text = _detail.ToString().TrimEnd('\n', '\r', ' ', '\t');
            e.Detail = text.Length == 0 ? null : text;
        }

        _current = null;
        _detail = null;
        _continuationLines = 0;
        _currentFull = false;
        return e;
    }

    private void AppendDetail(SourceLine line)
    {
        _detail ??= new StringBuilder();
        if (_detail.Length > 0 || _continuationLines > 0) _detail.Append('\n');
        _detail.Append(line.Text);
        _continuationLines++;
        _current!.LineEnd = line.LineNumber;
        _current.ByteEnd = line.ByteEnd;
    }

    private LogEvent NewRecord(SourceLine line, RecordHeader header)
    {
        var (timestamp, utcKnown) = Normalise(header);
        return new LogEvent
        {
            Kind = EventKind.Record,
            Level = header.Level,
            LevelText = header.LevelText,
            LineStart = line.LineNumber,
            LineEnd = line.LineNumber,
            ByteStart = line.ByteStart,
            ByteEnd = line.ByteEnd,
            Timestamp = timestamp,
            UtcKnown = utcKnown,
            TimestampText = FormatOriginal(header),
            Category = header.Category,
            Component = header.Component,
            Message = Truncate(header.Message, MaxMessageChars),
            HasException = LooksLikeException(header.Message),
        };
    }

    private (long Millis, bool UtcKnown) Normalise(RecordHeader header)
    {
        if (header.UtcOffset is { } offset)
        {
            var utc = new DateTimeOffset(header.LocalTimestamp, offset).UtcDateTime;
            return (ToMillis(utc), true);
        }

        if (_timeZone is not null)
        {
            var local = header.LocalTimestamp;
            // A wall-clock time skipped by a daylight-saving change cannot be converted; shift it forward by the gap.
            if (_timeZone.IsInvalidTime(local)) local = local.AddHours(1);
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, _timeZone);
            return (ToMillis(utc), true);
        }

        return (ToMillis(DateTime.SpecifyKind(header.LocalTimestamp, DateTimeKind.Utc)), false);
    }

    private static string FormatOriginal(RecordHeader h)
    {
        var text = h.LocalTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        if (h.UtcOffset is { } o) text += (o < TimeSpan.Zero ? " -" : " +") + o.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
        return text;
    }

    private static long ToMillis(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    internal static bool LooksLikeException(string text)
    {
        var t = text.AsSpan().TrimStart();
        if (t.StartsWith("at ", StringComparison.Ordinal) && t.Contains('(')) return true;
        if (t.StartsWith("--- End of inner exception", StringComparison.Ordinal)) return true;
        if (t.StartsWith("---> ", StringComparison.Ordinal)) return true;
        var colon = t.IndexOf(':');
        var head = colon > 0 ? t[..colon] : t;
        var space = head.IndexOf(' ');
        if (space >= 0) head = head[..space];
        return head.EndsWith("Exception", StringComparison.Ordinal) && head.Contains('.');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

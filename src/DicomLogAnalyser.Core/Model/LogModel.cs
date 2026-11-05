namespace DicomLogAnalyser.Core.Model;

/// <summary>Normalised severity. Numeric order is used for "at least" filters.</summary>
public enum EventLevel
{
    Unknown = 0,
    Trace = 1,
    Debug = 2,
    Info = 3,
    Warning = 4,
    Error = 5,
    Fatal = 6,
}

/// <summary>Whether an event was recognised by a format parser or is a block of lines no parser understood.</summary>
public enum EventKind
{
    Record = 0,
    Unparsed = 1,
}

/// <summary>Header fields parsed from the first line of a log record.</summary>
public sealed record RecordHeader(
    EventLevel Level,
    string LevelText,
    DateTime LocalTimestamp,
    TimeSpan? UtcOffset,
    string Category,
    string? Component,
    string Message);

/// <summary>A normalised log event with traceability to its source bytes and lines.</summary>
public sealed class LogEvent
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public long FileId { get; set; }
    public long LineStart { get; init; }
    public long LineEnd { get; set; }
    public long ByteStart { get; init; }
    public long ByteEnd { get; set; }

    /// <summary>Milliseconds since the Unix epoch: UTC when <see cref="UtcKnown"/>, otherwise source wall-clock time.</summary>
    public long? Timestamp { get; set; }

    public bool UtcKnown { get; set; }

    /// <summary>Timestamp exactly as written in the log line.</summary>
    public string? TimestampText { get; init; }

    public EventLevel Level { get; init; }
    public string LevelText { get; init; } = "";
    public string Category { get; init; } = "";
    public string? Component { get; init; }
    public string Message { get; set; } = "";

    /// <summary>Continuation lines (stack traces, wrapped text), newline-separated; null when there are none.</summary>
    public string? Detail { get; set; }

    public bool HasException { get; set; }
    public EventKind Kind { get; init; }
}

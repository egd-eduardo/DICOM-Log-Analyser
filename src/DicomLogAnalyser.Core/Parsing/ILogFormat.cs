using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Parsing;

/// <summary>
/// A log format. A format only has to recognise the first line of a record; the shared <see cref="RecordAssembler"/>
/// groups continuation lines (stack traces, wrapped text) and unrecognised lines. Add a new format by implementing this
/// interface and registering it in <see cref="FormatRegistry"/>, with synthetic fixtures and tests.
/// </summary>
public interface ILogFormat
{
    /// <summary>Stable identifier stored in the index (lower case, no spaces).</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Parses a record header line, or returns null when the line does not start a record of this format.</summary>
    RecordHeader? TryParseHeader(string line);
}

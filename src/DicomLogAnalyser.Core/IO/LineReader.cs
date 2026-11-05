using System.Text;

namespace DicomLogAnalyser.Core.IO;

/// <summary>One physical line with its exact position in the file.</summary>
public readonly record struct SourceLine(long LineNumber, long ByteStart, long ByteEnd, string Text, bool Terminated);

/// <summary>
/// Streams lines from a file starting at a byte offset without loading the file into memory. Line terminators are LF
/// or CRLF; the reported byte range includes the terminator. Text is decoded as strict UTF-8, falling back to
/// Windows-1252-compatible Latin-1 per line when the bytes are not valid UTF-8, so offsets always stay exact.
/// Very long lines are cut at <see cref="MaxLineBytes"/> (the remainder is skipped, the byte range stays exact).
/// </summary>
public sealed class LineReader : IDisposable
{
    public const int MaxLineBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly MemoryStream _line = new();
    private int _bufferLength;
    private int _bufferPos;
    private long _position;
    private long _lineNumber;
    private bool _eof;

    public LineReader(Stream stream, long startOffset, long startLineNumber)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        if (startOffset > 0) _stream.Seek(startOffset, SeekOrigin.Begin);
        _position = startOffset;
        _lineNumber = startLineNumber - 1;
    }

    /// <summary>Opens a log file strictly for reading, tolerating writers and rotation by the logging application.</summary>
    public static FileStream OpenReadOnly(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan,
        BufferSize = 0,
    });

    public long Position => _position;

    public SourceLine? ReadLine()
    {
        if (_eof && _bufferPos >= _bufferLength) return null;
        _line.SetLength(0);
        var start = _position;
        var lineBytes = 0L;
        while (true)
        {
            if (_bufferPos >= _bufferLength)
            {
                _bufferLength = _stream.Read(_buffer, 0, _buffer.Length);
                _bufferPos = 0;
                if (_bufferLength == 0)
                {
                    _eof = true;
                    if (lineBytes == 0) return null;
                    return Make(start, terminated: false);
                }
            }

            var span = _buffer.AsSpan(_bufferPos, _bufferLength - _bufferPos);
            var nl = span.IndexOf((byte)'\n');
            var take = nl >= 0 ? nl + 1 : span.Length;
            var keep = (int)Math.Max(0, Math.Min(take, MaxLineBytes - _line.Length));
            if (keep > 0) _line.Write(span[..keep]);
            _bufferPos += take;
            _position += take;
            lineBytes += take;
            if (nl >= 0) return Make(start, terminated: true);
        }
    }

    private SourceLine Make(long start, bool terminated)
    {
        _lineNumber++;
        var bytes = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        if (bytes.Length > 0 && bytes[^1] == '\n') bytes = bytes[..^1];
        if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
        // UTF-8 byte order mark at the start of the file.
        if (start == 0 && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        return new SourceLine(_lineNumber, start, _position, Decode(bytes), terminated);
    }

    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public void Dispose() => _line.Dispose();
}

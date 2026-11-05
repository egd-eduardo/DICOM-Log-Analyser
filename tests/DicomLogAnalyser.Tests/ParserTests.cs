using DicomLogAnalyser.Core.IO;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Parsing;

namespace DicomLogAnalyser.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("INF 2026.11.03 09:40:29 SPS - Study received", EventLevel.Info, "SPS", "Study received")]
    [InlineData("WARNING 2026.11.03 05:00:17 SML - Mail server not reachable", EventLevel.Warning, "SML", "Mail server not reachable")]
    [InlineData("ERROR 2026.11.03 09:41:02 SCU - C-STORE failed", EventLevel.Error, "SCU", "C-STORE failed")]
    [InlineData("INF 2026.11.03 09:48:00 FSP -", EventLevel.Info, "FSP", "")]
    public void B3dWeb_header_is_parsed(string line, EventLevel level, string category, string message)
    {
        var h = new B3dWebGatewayFormat().TryParseHeader(line);
        Assert.NotNull(h);
        Assert.Equal(level, h.Level);
        Assert.Equal(category, h.Category);
        Assert.Equal(message, h.Message);
        Assert.Null(h.UtcOffset);
        Assert.Equal(new DateTime(2026, 11, 3), h.LocalTimestamp.Date);
    }

    [Theory]
    [InlineData("   at System.Net.Sockets.Socket.Connect(EndPoint remoteEP)")]
    [InlineData("System.Net.Sockets.SocketException (10060): A connection attempt failed")]
    [InlineData("INF 2026.13.03 09:40:29 SPS - invalid month")]
    [InlineData("XYZ 2026.11.03 09:40:29 SPS - unknown level")]
    [InlineData("2026-10-27 00:00:06.059 +00:00 [INF] DicomRouter.Dicom.RouterScp: other format")]
    [InlineData("")]
    public void B3dWeb_rejects_non_headers(string line) => Assert.Null(new B3dWebGatewayFormat().TryParseHeader(line));

    [Fact]
    public void Router_header_is_parsed_with_offset_and_component()
    {
        var h = new DicomRouterFormat().TryParseHeader("2026-10-27 00:00:06.059 +00:00 [INF] DicomRouter.Dicom.RouterScp: Association accepted from TESTMOD01");
        Assert.NotNull(h);
        Assert.Equal(EventLevel.Info, h.Level);
        Assert.Equal("DicomRouter.Dicom.RouterScp", h.Component);
        Assert.Equal("RouterScp", h.Category);
        Assert.Equal(TimeSpan.Zero, h.UtcOffset);
        Assert.Equal("Association accepted from TESTMOD01", h.Message);
        Assert.Equal(59, h.LocalTimestamp.Millisecond);
    }

    [Theory]
    [InlineData("2026-10-27 00:00:07.120 +00:00 [ERR] DicomRouter.Forwarding.ForwardingService: failed", EventLevel.Error, "ForwardingService")]
    [InlineData("2026-10-27 00:00:09.001 -05:30 [WRN] DicomRouter.CloudGateway.CloudGatewayService: retry", EventLevel.Warning, "CloudGatewayService")]
    [InlineData("2026-10-27 00:01:15.448 +00:00 [INF] DicomRouter.QueryRetrieve.QueryRetrieveJob`1[DicomRouter.Model.Study]: done", EventLevel.Info, "QueryRetrieveJob")]
    [InlineData("2026-10-27 00:01:15.448 +00:00 [INF] Plain message: without component", EventLevel.Info, "(none)")]
    public void Router_levels_and_categories(string line, EventLevel level, string category)
    {
        var h = new DicomRouterFormat().TryParseHeader(line);
        Assert.NotNull(h);
        Assert.Equal(level, h.Level);
        Assert.Equal(category, h.Category);
    }

    [Fact]
    public void Router_line_with_empty_component_keeps_a_clean_message()
    {
        var h = new DicomRouterFormat().TryParseHeader("2026-10-27 00:00:06.059 +00:00 [FTL] : Host terminated unexpectedly")!;
        Assert.Equal(EventLevel.Fatal, h.Level);
        Assert.Null(h.Component);
        Assert.Equal("(none)", h.Category);
        Assert.Equal("Host terminated unexpectedly", h.Message);
    }

    [Fact]
    public void Router_negative_offset_is_applied()
    {
        var h = new DicomRouterFormat().TryParseHeader("2026-10-27 00:00:09.001 -05:30 [WRN] DicomRouter.X.Y: retry")!;
        Assert.Equal(-new TimeSpan(5, 30, 0), h.UtcOffset);
    }

    [Fact]
    public void Detection_picks_the_matching_format()
    {
        Assert.Equal(B3dWebGatewayFormat.FormatId, FormatRegistry.Detect(File.ReadLines(IndexFixture.Fixture("b3dweb-gateway/Gateway.log")))!.Id);
        Assert.Equal(DicomRouterFormat.FormatId, FormatRegistry.Detect(File.ReadLines(IndexFixture.Fixture("dicom-router/DicomRouter20261027.log")))!.Id);
        Assert.Null(FormatRegistry.Detect(["hello", "world"]));
    }

    [Fact]
    public void Line_reader_reports_exact_offsets_for_crlf_lf_bom_and_partial_lines()
    {
        byte[] bytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "first\r\nsecond\nthird"u8.ToArray()];
        using var ms = new MemoryStream(bytes);
        using var r = new LineReader(ms, 0, 1);
        var a = r.ReadLine()!.Value;
        var b = r.ReadLine()!.Value;
        var c = r.ReadLine()!.Value;
        Assert.Null(r.ReadLine());
        Assert.Equal(("first", 0L, 10L, 1L, true), (a.Text, a.ByteStart, a.ByteEnd, a.LineNumber, a.Terminated));
        Assert.Equal(("second", 10L, 17L, 2L), (b.Text, b.ByteStart, b.ByteEnd, b.LineNumber));
        Assert.Equal(("third", 17L, 22L, false), (c.Text, c.ByteStart, c.ByteEnd, c.Terminated));
    }

    [Fact]
    public void Line_reader_falls_back_to_latin1_for_invalid_utf8()
    {
        using var ms = new MemoryStream([0x43, 0x61, 0x66, 0xE9, 0x0A]);
        using var r = new LineReader(ms, 0, 1);
        Assert.Equal("Café", r.ReadLine()!.Value.Text);
    }

    [Fact]
    public void Line_reader_truncates_very_long_lines_but_keeps_offsets()
    {
        var data = new byte[LineReader.MaxLineBytes * 3 + 1];
        Array.Fill(data, (byte)'x');
        data[^1] = (byte)'\n';
        using var ms = new MemoryStream([.. data, .. "next\n"u8.ToArray()]);
        using var r = new LineReader(ms, 0, 1);
        var first = r.ReadLine()!.Value;
        Assert.Equal(LineReader.MaxLineBytes, first.Text.Length);
        Assert.Equal(data.Length, first.ByteEnd);
        Assert.Equal("next", r.ReadLine()!.Value.Text);
    }

    [Fact]
    public void Assembler_groups_stack_traces_and_keeps_leading_unknown_lines()
    {
        var lines = new[]
        {
            "garbage before the first record",
            "INF 2026.11.03 09:40:29 SPS - first",
            "ERROR 2026.11.03 09:41:02 SCU - failed",
            "System.Net.Sockets.SocketException (10060): timeout",
            "   at X.Y()",
            "INF 2026.11.03 09:45:00 SCH - after",
        };
        var asm = new RecordAssembler(new B3dWebGatewayFormat(), null);
        var events = new List<LogEvent>();
        long pos = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var len = lines[i].Length + 1;
            if (asm.Add(new SourceLine(i + 1, pos, pos + len, lines[i], true)) is { } e) events.Add(e);
            pos += len;
        }

        events.Add(asm.Close()!);
        Assert.Equal(4, events.Count);
        Assert.Equal(EventKind.Unparsed, events[0].Kind);
        Assert.Equal(1, asm.UnparsedLines);
        var err = events[2];
        Assert.Equal(EventLevel.Error, err.Level);
        Assert.True(err.HasException);
        Assert.Equal((3L, 5L), (err.LineStart, err.LineEnd));
        Assert.Contains("   at X.Y()", err.Detail, StringComparison.Ordinal);
        Assert.False(err.UtcKnown);
    }

    [Fact]
    public void Assembler_converts_local_times_with_configured_zone()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var asm = new RecordAssembler(new B3dWebGatewayFormat(), tz);
        asm.Add(new SourceLine(1, 0, 10, "INF 2026.07.01 12:00:00 SPS - summer", true));
        var e = asm.Close()!;
        Assert.True(e.UtcKnown);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 11, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), e.Timestamp);
    }
}

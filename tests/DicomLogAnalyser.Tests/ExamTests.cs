using DicomLogAnalyser.Core.Exam;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Sources;

namespace DicomLogAnalyser.Tests;

/// <summary>Exam history with the agreed definitions, on a synthetic exam across a Gateway and a Router log.</summary>
public class ExamTests
{
    private static long Utc(int h, int m, int s) => new DateTimeOffset(2026, 11, 3, h, m, s, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static (IndexFixture F, ExamHistoryService Exams) Setup(string? extraGatewayLine = null)
    {
        var f = new IndexFixture();
        var gw = f.Temp.Sub("gw");
        var rt = f.Temp.Sub("rt");
        File.Copy(IndexFixture.Fixture("exam/Gateway-exam.log"), Path.Combine(gw, "Gateway-exam.log"));
        if (extraGatewayLine is not null) File.AppendAllText(Path.Combine(gw, "Gateway-exam.log"), extraGatewayLine + "\r\n");
        File.Copy(IndexFixture.Fixture("exam/DicomRouter-exam.log"), Path.Combine(rt, "DicomRouter-exam.log"));
        foreach (var (label, path) in new[] { ("Gateway", gw), ("Router", rt) })
        {
            var id = f.Sources.Add(new SourceDefinition { Label = label, Kind = SourceKinds.Folder, Path = path, TimeZone = "Europe/Lisbon" });
            f.Ingestor.Scan(f.Sources.Get(id)!, null, CancellationToken.None);
        }

        return (f, new ExamHistoryService(f.Search));
    }

    [Fact]
    public void Applies_the_agreed_definitions()
    {
        var (f, exams) = Setup();
        using var _ = f;
        var h = exams.Build("SYNEXAM01");

        // Entered the queue: first time the study was received.
        Assert.Equal(Utc(10, 0, 0), h.FirstReceivedTs);
        Assert.False(h.FirstReceivedEstimated);
        Assert.Equal("Gateway", h.FirstReceivedSource);

        // Updated: a new series arrived after the first receipt (series 3, 20 minutes later).
        Assert.True(h.SeriesKnown);
        Assert.Equal(3, h.Series.Count);
        Assert.Equal(1, h.Updates);
        Assert.Equal([Utc(10, 20, 0)], h.UpdateTimes);
        Assert.Equal(["2.25.700001.3"], h.Series.Where(s => s.IsUpdate).Select(s => s.SeriesUid).ToArray());

        // Resent: repeated sends of the same series, per destination.
        var pacs = h.Destinations.Single(d => d.Destination == "TESTPACS");
        Assert.Equal((4, 1, 1, "success"), (pacs.Attempts, pacs.Resends, pacs.Failures, pacs.LastResult));
        Assert.Equal(Utc(10, 21, 0), pacs.LastResultTs);
        var cloud = h.Destinations.Single(d => d.Destination == "TESTCLOUD");
        Assert.Equal((2, 1, 1, "success"), (cloud.Attempts, cloud.Resends, cloud.Failures, cloud.LastResult));
        Assert.Equal(2, h.TotalResends);
        Assert.Equal(2, h.TotalFailures);

        // Final: last series arrival; the last entry (a duplicate notice) comes later.
        Assert.Equal(Utc(10, 20, 0), h.FinalSeriesTs);
        Assert.Equal(Utc(10, 30, 0), h.LastEntryTs);

        // Router entries name only the study UID and are joined through it; the other exam is not included.
        Assert.Equal(["2.25.700001"], h.RelatedStudyUids);
        Assert.Equal(2, h.Sources);
        Assert.Equal(14, h.Entries);
        Assert.DoesNotContain(h.Timeline, e => e.Event.Message.Contains("SYNOTHER9", StringComparison.Ordinal));
        Assert.True(h.UtcKnown);
    }

    [Fact]
    public void Identifiers_match_exactly()
    {
        var (f, exams) = Setup("INF 2026.11.03 11:00:00 SPS - Study received from AE TESTMOD01: Accession=SYNEXAM010 StudyInstanceUID=2.25.7000019 SeriesInstanceUID=2.25.7000019.1");
        using var _ = f;
        var h = exams.Build("SYNEXAM01");
        Assert.Equal(14, h.Entries);
        Assert.Equal(Utc(10, 30, 0), h.LastEntryTs);
        Assert.Equal(3, exams.Build("2.25.700001").Series.Count);
    }

    [Fact]
    public void Unknown_identifier_gives_an_empty_history_with_coverage_notes()
    {
        var (f, exams) = Setup();
        using var _ = f;
        var h = exams.Build("NOTINLOGS42");
        Assert.Equal(0, h.Entries);
        Assert.Null(h.FirstReceivedTs);
        Assert.Empty(h.Destinations);
        Assert.NotNull(h.CoverageNotes);
    }

    [Theory]
    [InlineData("Study received from AE TESTMOD01: Accession=X1 SeriesInstanceUID=2.25.500001.1", EventLevel.Info, Milestone.Received, "2.25.500001.1", null)]
    [InlineData("C-STORE to TESTPACS failed for Accession=X1", EventLevel.Error, Milestone.SendFailed, null, "TESTPACS")]
    [InlineData("C-STORE to TESTPACS succeeded for Accession=X1 SeriesInstanceUID=2.25.500001.1", EventLevel.Info, Milestone.SendSucceeded, "2.25.500001.1", "TESTPACS")]
    [InlineData("Forwarding of study 2.25.500001 to destination TESTCLOUD started", EventLevel.Info, Milestone.SendAttempt, null, "TESTCLOUD")]
    [InlineData("Retry 1 of 5 for study 2.25.500001 destination TESTCLOUD", EventLevel.Warning, Milestone.Retry, null, "TESTCLOUD")]
    [InlineData("Duplicate SeriesInstanceUID=2.25.500001.1 ignored", EventLevel.Info, Milestone.Duplicate, "2.25.500001.1", null)]
    [InlineData("Study queued for routing", EventLevel.Info, Milestone.Queued, null, null)]
    [InlineData("Free disk space 81 percent", EventLevel.Info, Milestone.Other, null, null)]
    public void Classifies_entries_by_wording(string message, EventLevel level, Milestone expected, string? series, string? destination)
    {
        var e = new EventSummary(1, 1, "S", "f.log", 1, 1, 0, true, null, level, "INF", EventKind.Record, "X", null, message, false, false);
        var c = ExamHistoryService.Classify(e);
        Assert.Equal((expected, series, destination), (c.Milestone, c.SeriesUid, c.Destination));
    }

    [Fact]
    public void Message_patterns_contain_no_identifiers()
    {
        var (f, _) = Setup();
        using var __ = f;
        var rows = new OverviewService(f.Db).MessagePatterns();
        Assert.NotEmpty(rows);
        var all = string.Join("\n", rows.Select(r => r.Pattern));
        foreach (var secret in new[] { "SYNEXAM01", "2.25.700001", "SYNTHETIC^EXAM", "192.0.2.10", "TESTMOD01 (" })
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        Assert.Contains(rows, r => r.Pattern.Contains("C-STORE to TESTPACS failed", StringComparison.Ordinal) && r.Level == "error");
    }

    // ---- B3DWEB Gateway wording (synthetic copy of the real layout) ----

    private static long Nov4(int h, int m, int s) => new DateTimeOffset(2026, 11, 4, h, m, s, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static (IndexFixture F, ExamHistoryService Exams) SetupGateway()
    {
        var f = new IndexFixture();
        var gw = f.Temp.Sub("b3d");
        File.Copy(IndexFixture.Fixture("exam/Gateway-b3dweb-exam.log"), Path.Combine(gw, "Gateway.log"));
        var id = f.Sources.Add(new SourceDefinition { Label = "Gateway", Kind = SourceKinds.Folder, Path = gw, TimeZone = "Europe/Lisbon" });
        f.Ingestor.Scan(f.Sources.Get(id)!, null, CancellationToken.None);
        return (f, new ExamHistoryService(f.Search));
    }

    [Fact]
    public void Gateway_queries_and_retrievals_are_not_receipts_and_earlier_activity_is_reported()
    {
        var (f, exams) = SetupGateway();
        using var _ = f;
        var h = exams.Build("SYNA0001REP");

        Assert.Equal(19, h.Entries);
        Assert.Equal(6, h.Timeline.Count(e => e.Milestone == Milestone.Query));
        Assert.Equal(4, h.Timeline.Count(e => e.Milestone == Milestone.Retrieve));
        Assert.Equal(4, h.Timeline.Count(e => e.Milestone == Milestone.Received));

        // Entered the queue at the first receipt of images, not at the queries or the C-MOVE hours earlier.
        Assert.Equal(Nov4(8, 16, 19), h.FirstReceivedTs);
        Assert.Equal(Nov4(3, 3, 50), h.PriorActivityTs);
        Assert.Equal(0, h.Updates);
        Assert.Equal(["2.25.900001.11", "2.25.900001.12", "2.25.900001.13", "2.25.900001.14"], h.Series.Select(x => x.SeriesUid).ToArray());
        Assert.Equal(Nov4(8, 16, 31), h.FinalSeriesTs);
        Assert.Equal(Nov4(8, 16, 40), h.FirstQueuedTs);
        Assert.Equal(Nov4(8, 17, 30), h.LastEntryTs);

        // "(5425 OK, 0 Warn, 0 Fail),Status Success" is a success; "to be uploaded" names no destination.
        Assert.Equal(["Cloud upload", "TESTCACHE"], h.Destinations.Select(d => d.Destination).ToArray());
        var cache = h.Destinations.Single(d => d.Destination == "TESTCACHE");
        Assert.Equal((1, 0, 0, "success", Nov4(3, 9, 27)), (cache.Attempts, cache.Resends, cache.Failures, cache.LastResult, cache.LastResultTs!.Value));
        var upload = h.Destinations.Single(d => d.Destination == ExamHistoryService.UploadDestination);
        Assert.Equal((1, 0, 0, "success"), (upload.Attempts, upload.Resends, upload.Failures, upload.LastResult));
        Assert.Equal(0, h.TotalFailures);
        Assert.Equal(["2.25.900001"], h.RelatedStudyUids);
        Assert.DoesNotContain(h.Timeline, e => e.Event.Message.Contains("SYNA0009REP", StringComparison.Ordinal));
    }

    [Fact]
    public void Gateway_update_wave_and_second_upload_is_not_a_resend()
    {
        var (f, exams) = SetupGateway();
        using var _ = f;
        var h = exams.Build("SYNA0002REP");

        Assert.Equal(13, h.Entries);
        Assert.Equal(Nov4(8, 13, 20), h.FirstReceivedTs);
        Assert.Null(h.PriorActivityTs);
        Assert.Equal(1, h.Updates);
        Assert.Equal([Nov4(8, 40, 0)], h.UpdateTimes);
        Assert.Equal(7, h.Series.Count);
        Assert.Equal(["2.25.900002.7"], h.Series.Where(x => x.IsUpdate).Select(x => x.SeriesUid).ToArray());
        Assert.Equal(Nov4(8, 40, 0), h.FinalSeriesTs);
        Assert.Equal(Nov4(8, 14, 25), h.FirstQueuedTs);
        var upload = h.Destinations.Single();
        Assert.Equal((ExamHistoryService.UploadDestination, 2, 0, 0, "success"), (upload.Destination, upload.Attempts, upload.Resends, upload.Failures, upload.LastResult));
        Assert.Equal(Nov4(8, 41, 0), h.LastEntryTs);
    }

    [Theory]
    [InlineData("C-STORE-RQ (Single Send) sending ... done! [Dest AET TESTCACHE] 10 messages (10 OK, 0 Warn, 0 Fail),Status Success", Milestone.SendSucceeded, "TESTCACHE")]
    [InlineData("C-STORE-RQ (Single Send) sending ... done! [Dest AET TESTCACHE] 10 messages (7 OK, 0 Warn, 3 Fail),Status Failure", Milestone.SendFailed, "TESTCACHE")]
    [InlineData("Received Study Root Query/Retrieve Information Model - FIND - STUDY from TESTCACHE with attributes AccessionNumber - SYNA1REP ... send 1 reponse(s) complete", Milestone.Query, null)]
    [InlineData("Received CMove Study Root Query/Retrieve Information Model - MOVE - STUDY from TESTCACHE", Milestone.Retrieve, null)]
    [InlineData("Adding upload job TESTMOD01 2.25.900003 SYNA3REP dicom to be uploaded 5 skipped 0", Milestone.Queued, null)]
    [InlineData("End upload (0005) SYNA3REP SYNTHETIC^X CT Site 2.25.900003  successfully uploaded.", Milestone.SendSucceeded, "Cloud upload")]
    [InlineData("Receiving (1) ... PID SYNPID3, ANR SYNA3REP, Mod CT, TESTMOD01 192.0.2.1:5 -> GATEWAYAE 192.0.2.2:104, #Ser 1, StUID 2.25.900003, SeUID 2.25.900003.1", Milestone.Received, null)]
    public void Classifies_gateway_wording(string message, Milestone expected, string? destination)
    {
        var e = new EventSummary(1, 1, "s", "f", 1, 1, 0, true, null, EventLevel.Info, "INF", EventKind.Record, "SCU", null, message, false, false);
        var c = ExamHistoryService.Classify(e);
        Assert.Equal(expected, c.Milestone);
        Assert.Equal(destination, c.Destination);
    }

    [Theory]
    [InlineData("C-MOVE SYNA0001REP SYNTHETIC^PATIENT ONE TWO Vascular^Angio_Test (Adult) CT 2.25.900001 ...")]
    [InlineData("Beg upload (0987) SYNA0002REP SYNTHETIC^PATIENT ONE TWO SR Example Site Thorax^Test (Adult) 2.25.900002  ... IsLAN:False HttpClient")]
    public void Masked_patterns_remove_every_part_of_a_multi_word_name(string message)
    {
        var sig = OverviewService.Signature(message);
        foreach (var part in new[] { "SYNTHETIC", "PATIENT", "ONE", "TWO", "SYNA", "2.25" })
            Assert.DoesNotContain(part, sig, StringComparison.Ordinal);
    }
}

using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Worklist;

namespace DicomLogAnalyser.Tests;

/// <summary>Activity over time (Overview chart): classification by wording, time slots, silent periods.</summary>
public class ActivityTests
{
    [Theory]
    [InlineData("Association accepted from TESTMOD01 at 192.0.2.10:104", ActivityKind.Association)]
    [InlineData("Association(101) accepted 192.0.2.21:50101", ActivityKind.Association)]
    [InlineData("Association(101): A-ASSOCIATE-RQ TESTMR1 >> RIS", ActivityKind.None)]
    [InlineData("TESTMR1(101): A-ASSOCIATE-AC RIS >> TESTMR1", ActivityKind.None)]
    [InlineData("TESTMR1(101) >> 1:C-FIND-RQ [pcid=1, prior=0]", ActivityKind.Query)]
    [InlineData("TESTMR1(101) << 1:C-FIND-RSP [pcid=1, status=FF00H]", ActivityKind.None)]
    [InlineData("Received Study Root Query/Retrieve Information Model - FIND - STUDY from TESTCACHE", ActivityKind.Query)]
    [InlineData("Received CMove Study Root Query/Retrieve Information Model - MOVE - STUDY from TESTCACHE", ActivityKind.Retrieve)]
    [InlineData("C-GET request from TESTWS01", ActivityKind.Retrieve)]
    [InlineData("C-STORE-RQ (Single Send) sending ... done! [Send ID 1, C-MOVE sub-operation]", ActivityKind.Sent)]
    [InlineData("Study received from AE TESTMOD01 (192.0.2.10): Accession=SYNACC0001", ActivityKind.Received)]
    [InlineData("C-STORE received SOPInstanceUID=2.25.1234567", ActivityKind.Received)]
    [InlineData("Receiving (1) ... PID SYNPID0002, Mod CT, TESTMOD01 -> GATEWAYAE", ActivityKind.Received)]
    [InlineData("C-STORE to TESTPACS failed for Accession=SYNACC0001", ActivityKind.Sent)]
    [InlineData("Forwarding of study 2.25.700001 to destination TESTCLOUD started", ActivityKind.Sent)]
    [InlineData("End upload (0987) SYNA0002REP successfully uploaded", ActivityKind.Sent)]
    [InlineData("Mail server smtp.example not reachable, retrying in 60 s", ActivityKind.None)]
    [InlineData("", ActivityKind.None)]
    public void Classifies_lines_by_wording(string message, ActivityKind expected) => Assert.Equal(expected, ActivityClassifier.Classify(message));

    [Fact]
    public void Worklist_associations_match_the_worklist_analysis()
    {
        using var f = new IndexFixture();
        File.Copy(IndexFixture.Fixture("vidacare-connector/VIDAcare.Connector.log"), Path.Combine(f.LogDir, "VIDAcare.Connector.log"));
        f.Scan(f.AddSource("Europe/Lisbon"));
        var a = new ActivityService(f.Db).Build(null, null);
        var worklist = new WorklistAnalysis(f.Db).Analyse(null, null);
        Assert.Equal(worklist.Equipment.Sum(e => e.Associations), a.Totals.Associations);
        Assert.Equal(worklist.Equipment.Sum(e => e.Queries), a.Totals.Queries);
        Assert.Equal(0, a.Totals.Received);
    }

    [Fact]
    public void Gateway_exam_fixture_counts_received_sent_and_errors()
    {
        using var f = new IndexFixture();
        File.Copy(IndexFixture.Fixture("exam/Gateway-exam.log"), Path.Combine(f.LogDir, "Gateway-exam.log"));
        f.Scan(f.AddSource("Europe/Lisbon"));
        var a = new ActivityService(f.Db).Build(24, null);
        Assert.Equal((4, 5, 1), (a.Totals.Received, a.Totals.Sent, a.Totals.Errors));
        Assert.Equal(f.Count(), a.Totals.Events);
        Assert.Equal(a.Totals.Events, a.Buckets.Sum(b => b.Events));
        Assert.False(a.LocalTimes);
    }

    [Fact]
    public void Period_ends_at_the_newest_event_and_finds_silent_periods()
    {
        using var f = new IndexFixture();
        // Old logs (2025): "the last 24 hours" means the last 24 hours of the logs, not of the clock.
        File.WriteAllText(Path.Combine(f.LogDir, "Gateway.log"), string.Join('\n',
            "INF 2025.03.01 10:00:00 SPS - Study received from AE TESTMOD01 (192.0.2.10): Accession=SYNACC0001",
            "INF 2025.03.01 10:05:00 SCU - C-STORE to TESTPACS succeeded for Accession=SYNACC0001",
            "ERROR 2025.03.01 14:00:00 SCU - C-STORE to TESTPACS failed for Accession=SYNACC0002",
            "INF 2025.03.01 14:01:00 QRY - C-FIND from AE TESTWS02 (192.0.2.21): Accession=SYNACC0002, 0 matches") + "\n");
        var source = f.AddSource("UTC");
        f.Scan(source);
        var service = new ActivityService(f.Db);
        var a = service.Build(24, null);

        Assert.Equal(15 * 60_000, a.BucketMs);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), a.FromMs);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 14, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), a.ToMs);
        Assert.Equal(17, a.Buckets.Count);
        Assert.Equal((1, 2, 1, 1), (a.Totals.Received, a.Totals.Sent, a.Totals.Queries, a.Totals.Errors));
        var silent = Assert.Single(a.Silent);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 10, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), silent.FromMs);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), silent.ToMs);

        // Never more than the maximum number of slots, also for everything indexed; unknown source is empty.
        Assert.InRange(service.Build(null, null).Buckets.Count, 1, ActivityService.MaxBuckets);
        Assert.Empty(service.Build(24, source.Id + 99).Buckets);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Build(0, null));

        // The cache follows new data.
        File.AppendAllText(Path.Combine(f.LogDir, "Gateway.log"), "INF 2025.03.01 14:02:00 SPS - Study received from AE TESTMOD01 (192.0.2.10): Accession=SYNACC0003\n");
        f.Scan(source);
        Assert.Equal(2, service.Build(24, null).Totals.Received);
    }

    [Fact]
    public void Empty_index_gives_an_empty_chart()
    {
        using var f = new IndexFixture();
        var a = new ActivityService(f.Db).Build(24, null);
        Assert.Empty(a.Buckets);
        Assert.Null(a.FromMs);
    }
}

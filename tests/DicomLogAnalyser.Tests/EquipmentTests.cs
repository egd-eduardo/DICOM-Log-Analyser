using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace DicomLogAnalyser.Tests;

/// <summary>Equipment (modality and AE title) named in the logs, and the equipment filters.</summary>
public class EquipmentTests
{
    [Theory]
    [InlineData("Receiving (1) ... PID SYNPID0002, Mod US, PHILLIS4", "US", "PHILLIS4", null)]
    [InlineData("Receiving (1) ... Mod CT, TESTMOD01 192.0.2.121:51081 -> GATEWAYAE", "CT", "TESTMOD01", "192.0.2.121")]
    [InlineData("Modality: MR; TESTMR1", "MR", "TESTMR1", null)]
    [InlineData("Mod=DX,TEST_DX-1 done", "DX", "TEST_DX-1", null)]
    public void Reads_modality_and_ae_title(string line, string modality, string aet, string? ip)
    {
        var hit = EquipmentService.Parse(line);
        Assert.NotNull(hit);
        Assert.Equal((modality, aet, ip), (hit.Value.Modality, hit.Value.Aet, hit.Value.Address));
    }

    [Theory]
    [InlineData("Mod XYZ, TESTMOD01")]
    [InlineData("Model US, PHILLIS4")]
    [InlineData("Mod US, 12345")]
    [InlineData("mod us, phillis4")]
    [InlineData("")]
    public void Ignores_lines_without_a_modality_code_and_ae_title(string line) => Assert.Null(EquipmentService.Parse(line));

    [Theory]
    [InlineData("PHILLIS4", true)]
    [InlineData("AE WITH SPACE", true)]
    [InlineData("SEVENTEEN_CHARS_X", false)]
    [InlineData("A,B", false)]
    [InlineData("A\\B", false)]
    [InlineData("A\"B", false)]
    [InlineData(" LEAD", false)]
    [InlineData("", false)]
    public void Validates_ae_titles(string aet, bool valid) => Assert.Equal(valid, EquipmentService.IsValidAet(aet));

    private static IndexFixture Indexed(params string[] lines)
    {
        var f = new IndexFixture();
        var copy = Path.Combine(f.LogDir, "Gateway-b3dweb-exam.log");
        File.Copy(IndexFixture.Fixture("exam/Gateway-b3dweb-exam.log"), copy);
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow); // same day as the extra file, so both are chosen for analysis
        if (lines.Length > 0) File.WriteAllText(Path.Combine(f.LogDir, "Gateway-extra.log"), string.Join('\n', lines) + "\n");
        f.Scan(f.AddSource("Europe/Lisbon"));
        return f;
    }

    [Fact]
    public void Lists_equipment_with_modalities_and_addresses()
    {
        using var f = Indexed(
            "INF 2026.11.04 09:00:00 SPS - Receiving (1) ... PID SYNPID0009, ANR SYNA0009REP, StDate 2026.11.04 08:55:00, Mod US, PHILLIS4 192.0.2.140:50100 -> GATEWAYAE 192.0.2.1:104",
            "INF 2026.11.04 09:00:05 SPS - Study received from AE PHILLIS4 (192.0.2.140): Accession=SYNA0009REP",
            "INF 2026.11.04 09:01:00 SCU - C-STORE to TESTPACS succeeded for Accession=SYNA0009REP");
        var list = new EquipmentService(f.Db).List();
        Assert.Equal(["TESTMOD01", "PHILLIS4"], list.Select(e => e.Aet));
        var mod01 = list[0];
        Assert.Equal(["CT", "SR"], mod01.Modalities.Keys.Order());
        Assert.Equal(["192.0.2.121"], mod01.Addresses);
        Assert.True(mod01.Modalities["CT"] > mod01.Modalities["SR"]);
        var us = list[1];
        Assert.Equal(1, us.Modalities["US"]);
        Assert.Equal(["192.0.2.140"], us.Addresses);

        // The chart filter counts only lines naming the AE title; silent periods are not reported for it.
        var activity = new ActivityService(f.Db);
        var all = activity.Build(null, null);
        var phil = activity.Build(null, null, ["PHILLIS4"]);
        Assert.Equal(2, phil.Totals.Events);
        Assert.Equal(2, phil.Totals.Received);
        Assert.Equal(0, phil.Totals.Sent);
        Assert.Empty(phil.Silent);
        Assert.Equal(all.ToMs, phil.ToMs);
        var lower = activity.Build(null, null, ["phillis4"]);
        Assert.Equal(2, lower.Totals.Events);
        Assert.Equal(0, activity.Build(null, null, ["PHILLIS"]).Totals.Events);
        Assert.Throws<ArgumentException>(() => activity.Build(null, null, ["A,B"]));
    }

    [Fact]
    public void Search_filter_adds_the_ae_title_as_a_required_term()
    {
        var (request, error) = Endpoints.ParseSearch(new QueryCollection(new Dictionary<string, StringValues> { ["q"] = "SYNA0009REP", ["aet"] = "PHILLIS4" }));
        Assert.Null(error);
        Assert.Equal("SYNA0009REP \"PHILLIS4\"", request!.Text);
        Assert.Equal("Invalid AE title.", Endpoints.ParseSearch(new QueryCollection(new Dictionary<string, StringValues> { ["aet"] = "A\"B" })).Error);

        using var f = Indexed("INF 2026.11.04 09:00:05 SPS - Study received from AE PHILLIS4 (192.0.2.140): Accession=SYNA0009REP");
        var result = f.Search.Search(Endpoints.ParseSearch(new QueryCollection(new Dictionary<string, StringValues> { ["aet"] = "PHILLIS4" })).Request!);
        Assert.Single(result.Items);
    }
}

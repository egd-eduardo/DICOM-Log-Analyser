using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Parsing;
using DicomLogAnalyser.Core.Worklist;

namespace DicomLogAnalyser.Tests;

/// <summary>Modality worklist analysis on a synthetic VIDAcare Connector log with staged AE title inconsistencies.</summary>
public class WorklistTests
{
    private static WorklistReport Analyse()
    {
        using var f = new IndexFixture();
        File.Copy(IndexFixture.Fixture("vidacare-connector/VIDAcare.Connector.log"), Path.Combine(f.LogDir, "VIDAcare.Connector.log"));
        var s = f.AddSource("Europe/Lisbon");
        f.Ingestor.Scan(s, null, CancellationToken.None);
        return new WorklistAnalysis(f.Db).Analyse(null, null);
    }

    [Fact]
    public void Header_is_parsed_and_the_memory_counter_removed()
    {
        var h = new VidacareConnectorFormat().TryParseHeader("2026-11-05 09:00:00,123 [MR1:22] INFO  Association - MR1(7): A-RELEASE-RQ MR1 << RIS | 6 907 552");
        Assert.NotNull(h);
        Assert.Equal(EventLevel.Info, h.Level);
        Assert.Equal("Association", h.Category);
        Assert.Equal("MR1:22", h.Component);
        Assert.Equal("MR1(7): A-RELEASE-RQ MR1 << RIS", h.Message);
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0, 123), h.LocalTimestamp);
        Assert.Null(h.UtcOffset);
        Assert.Null(new VidacareConnectorFormat().TryParseHeader("(0008,0050) SH #0 [] Accession Number"));
        Assert.Null(new VidacareConnectorFormat().TryParseHeader("INF 2026.11.03 10:00:00 SPS - not this format"));
    }

    [Fact]
    public void Detects_the_format_from_content()
    {
        var lines = File.ReadLines(IndexFixture.Fixture("vidacare-connector/VIDAcare.Connector.log")).Take(200).Select(l => l.TrimStart('﻿'));
        Assert.Equal(VidacareConnectorFormat.FormatId, FormatRegistry.Detect(lines)?.Id);
    }

    [Fact]
    public void Summarises_each_piece_of_equipment()
    {
        var r = Analyse();
        Assert.True(r.DimseDetail);
        Assert.False(r.Truncated);
        Assert.Equal(["AE_TESTMG", "PHILUS1", "TESTCT1", "TESTCT2", "TESTDX1", "TESTMR1", "TESTUS1", "TESTUS2"], r.Equipment.Select(e => e.CallingAe).ToArray());

        var mr = r.Equipment.Single(e => e.CallingAe == "TESTMR1");
        Assert.Equal((2, 2, 2, 1, 0), (mr.Associations, mr.Queries, mr.Items, mr.EmptyAnswers, mr.Failures));
        Assert.Equal(["192.0.2.21"], mr.Addresses);
        Assert.Equal(["SYNTHETIC_MR_1"], mr.Implementations);
        Assert.Equal(["RIS"], mr.CalledAes);
        Assert.Equal(2, mr.QueryModalities["MR"]);
        Assert.Equal(2, mr.QueryStationAes["(any)"]);
        Assert.Equal(2, mr.ItemStationAes["TESTMR1"]);
        Assert.Equal(["TESTMR1"], mr.WorklistFor);

        var phil = r.Equipment.Single(e => e.CallingAe == "PHILUS1");
        Assert.Equal((2, 1, 1, 1), (phil.Associations, phil.Echoes, phil.Queries, phil.Items));
        Assert.Equal(1, r.Equipment.Single(e => e.CallingAe == "TESTDX1").Failures);

        // The connector's own outgoing query is not equipment.
        Assert.DoesNotContain(r.Equipment, e => e.CallingAe == "TESTQR");
        Assert.All(r.Recent, a => Assert.False(a.Outbound));
        Assert.Equal(12, r.Recent.Count);
    }

    [Theory]
    [InlineData("never-items", "AE_TESTMG")]
    [InlineData("ae-generic", "AE_TESTMG")]
    [InlineData("ae-vendor", "PHILUS1")]
    [InlineData("ae-several-addresses", "TESTCT1")]
    [InlineData("address-several-aes", "TESTUS1")]
    [InlineData("query-station-ae", "TESTCT2")]
    [InlineData("item-station-ae", "TESTCT2")]
    [InlineData("called-ae", "TESTCT2")]
    [InlineData("failures", "TESTDX1")]
    public void Points_out_ae_title_inconsistencies(string code, string ae)
    {
        var r = Analyse();
        Assert.Contains(r.Findings, x => x.Code == code && x.AeTitles.Contains(ae));
    }

    [Fact]
    public void Consistent_equipment_has_no_findings()
    {
        var r = Analyse();
        Assert.DoesNotContain(r.Findings, x => x.AeTitles.Contains("TESTMR1"));
        Assert.Equal("error", r.Findings[0].Severity);
    }

    [Fact]
    public void Period_limits_the_analysis_and_empty_index_reports_nothing()
    {
        using var f = new IndexFixture();
        var empty = new WorklistAnalysis(f.Db).Analyse(null, 24);
        Assert.Null(empty.ToTs);
        Assert.Empty(empty.Equipment);

        File.Copy(IndexFixture.Fixture("vidacare-connector/VIDAcare.Connector.log"), Path.Combine(f.LogDir, "c.log"));
        f.Ingestor.Scan(f.AddSource("Europe/Lisbon"), null, CancellationToken.None);
        var lastHour = new WorklistAnalysis(f.Db).Analyse(null, 1);
        Assert.Equal(lastHour.ToTs - 3_600_000L, lastHour.FromTs);
        Assert.Equal(12, lastHour.Recent.Count); // the fixture spans 12 minutes
    }

    [Theory]
    [InlineData("Data from server - [room=(null),modality=MR,procedureCode=SYN1,code=7,patientName=SYNTHETIC PERSON ONE,patientId=X]", "SYNTHETIC")]
    [InlineData("Order for PatientName: SYNTHETIC PERSON TWO; ward 3", "PERSON")]
    public void Masked_patterns_remove_key_value_names(string message, string secret)
    {
        var sig = Core.Indexing.OverviewService.Signature(message);
        Assert.DoesNotContain(secret, sig, StringComparison.Ordinal);
        Assert.Contains("<name>", sig, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_is_named_only_as_a_value()
    {
        var r = Analyse();
        Assert.Equal(["VIDAcare"], r.Providers);
        Assert.Contains(r.SupportedProviders, p => p.Name == "VIDAcare" && p.FormatId == VidacareConnectorFormat.FormatId);
        Assert.All(r.Recent, a => Assert.Equal("VIDAcare", a.Provider));

        // Findings and the interface speak of "the worklist provider", never of a product.
        foreach (var provider in WorklistProviders.All)
        {
            Assert.DoesNotContain(r.Findings, x => x.Title.Contains(provider.Name, StringComparison.OrdinalIgnoreCase)
                || x.Detail.Contains(provider.Name, StringComparison.OrdinalIgnoreCase));
            // The interface as shipped (embedded in the program).
            foreach (var resource in new[] { "ui/index.html", "ui/app.js", "ui/exam-i18n.json" })
            {
                using var stream = typeof(DicomLogAnalyser.Web.EmbeddedUi).Assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                Assert.DoesNotContain(provider.Name, reader.ReadToEnd(), StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.StartsWith("Worklist provider log", new VidacareConnectorFormat().DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_index_still_lists_the_supported_providers()
    {
        using var f = new IndexFixture();
        var r = new WorklistAnalysis(f.Db).Analyse(null, 24);
        Assert.Empty(r.Providers);
        Assert.NotEmpty(r.SupportedProviders);
    }
}

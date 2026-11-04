using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Web.Api;

namespace DicomLogAnalyser.Tests;

public class QueryTests
{
    [Theory]
    [InlineData("1.2.840.10008", "\"1 2 840 10008\"")]
    [InlineData("SYNACC0001 TESTPACS", "\"synacc0001\" AND \"testpacs\"")]
    [InlineData("\"C-STORE failed\"", "\"c store failed\"")]
    [InlineData("SYNACC*", "\"synacc\"*")]
    [InlineData("store -echo", "\"store\" NOT \"echo\"")]
    [InlineData("DOE^JANE", "\"doe jane\"")]
    [InlineData("\" OR 1=1 --", "\"or 1 1\"")]
    [InlineData("NEAR(a b) col:x", "\"near a\" AND \"b\" AND \"col x\"")]
    public void Builds_safe_fts_expressions(string input, string expected) => Assert.Equal(expected, FtsQuery.Build(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("^^^ ...")]
    [InlineData("-onlynegative")]
    public void Empty_or_punctuation_only_input_is_ignored(string input) => Assert.Null(FtsQuery.Build(input));

    [Fact]
    public void Hostile_queries_do_not_throw()
    {
        using var f = new IndexFixture();
        File.WriteAllText(Path.Combine(f.LogDir, "g.log"), "INF 2026.11.03 09:40:29 SPS - hello world\n");
        f.Scan(f.AddSource());
        foreach (var q in new[] { "\"", "*", "a OR", "(", ")", "NOT", "{x}", "a:b", "\0", new string('a', 600), "AND AND" })
            _ = f.Search.Search(new SearchRequest { Text = q });
        Assert.Single(f.Search.Search(new SearchRequest { Text = "HELLO" }).Items);
    }

    [Fact]
    public void Error_signature_masks_identifiers()
    {
        var sig = Core.Indexing.OverviewService.Signature("C-STORE to TESTPACS failed for Accession=SYNACC0001 SOPInstanceUID=2.25.299410238811740092734811920051234567 patient SYNTHETIC^ALPHA from 192.0.2.10");
        Assert.DoesNotContain("SYNACC0001", sig, StringComparison.Ordinal);
        Assert.DoesNotContain("2.25.", sig, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA", sig, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", sig, StringComparison.Ordinal);
        Assert.Contains("SOPInstanceUID=<uid/ip>", sig, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "\"'=cmd|' /C calc'!A0\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("line1\nline2", "\"line1 line2\"")]
    public void Csv_cells_are_quoted_and_formula_safe(string input, string expected) => Assert.Equal(expected, Endpoints.Csv(input));

    [Fact]
    public void Search_parameters_are_validated()
    {
        var q = new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["minLevel"] = "warning",
            ["from"] = "2026-11-03T09:00:00Z",
            ["sources"] = "1,2",
        });
        var (req, err) = Endpoints.ParseSearch(q);
        Assert.Null(err);
        Assert.Equal(EventLevel.Warning, req!.MinLevel);
        Assert.Equal([1L, 2L], req.SourceIds);
        Assert.NotNull(Endpoints.ParseSearch(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["minLevel"] = "5" })).Error);
        Assert.NotNull(Endpoints.ParseSearch(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["sources"] = "1;drop" })).Error);
    }
}

public class SafeErrorTests
{
    [Fact]
    public void Describes_sqlite_errors_by_code_without_message_text()
    {
        var ex = new Microsoft.Data.Sqlite.SqliteException("near \"SYNACC0001\": syntax error in /secret/path", 5, 517);
        var text = Core.Diagnostics.SafeError.Describe(ex);
        Assert.Equal("SqliteException (SQLite 5/517)", text);
        Assert.True(Core.Diagnostics.SafeError.IsTransientStorageError(ex));
        Assert.False(Core.Diagnostics.SafeError.IsTransientStorageError(new IOException("x")));
    }
}

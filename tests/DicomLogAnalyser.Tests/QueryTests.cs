using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Web.Api;

namespace DicomLogAnalyser.Tests;

public class QueryTests
{
    [Theory]
    [InlineData("1.2.840.10008", "\"1.2.840.10008\"")]
    [InlineData("SYNACC0001 TESTPACS", "\"SYNACC0001\" AND \"TESTPACS\"")]
    [InlineData("\"C-STORE failed\"", "\"C-STORE failed\"")]
    [InlineData("SYNACC*", "\"SYNACC\"")]
    [InlineData("store -echo", "\"store\" NOT \"echo\"")]
    [InlineData("DOE^JANE", "\"DOE^JANE\"")]
    [InlineData("a\"b\"c", "\"a\"\"b\"\"c\"")]
    [InlineData("NEAR(abc) col:xyz", "\"NEAR(abc)\" AND \"col:xyz\"")]
    public void Builds_safe_literal_full_text_expressions(string input, string expected) => Assert.Equal(expected, FtsQuery.Parse(input).Match);

    [Fact]
    public void Short_terms_and_lone_exclusions_use_the_text_scan()
    {
        var q = FtsQuery.Parse("SC -ok");
        Assert.Null(q.Match);
        Assert.Equal(["SC"], q.ShortIncluded);
        Assert.Equal(["ok"], q.ShortExcluded);
        Assert.Equal("%50\\%\\_x%", FtsQuery.LikePattern("50%_x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Empty_input_is_ignored(string input) => Assert.True(FtsQuery.Parse(input).IsEmpty);

    [Theory]
    [InlineData("840.1136", 1)]
    [InlineData("2.25.29941", 1)]
    [InlineData("ACC000", 5)]
    [InlineData("synacc0001", 4)]
    [InlineData("acao", 1)]
    [InlineData("Ação", 1)]
    [InlineData("SocketException", 1)]
    [InlineData("Accession=SYNACC0001 instances", 1)]
    [InlineData("\"to TESTPACS\"", 3)]
    [InlineData("SC", 3)]
    [InlineData("SYNACC0001 -succeeded", 3)]
    [InlineData("SYNACC0001 -ok", 4)]
    [InlineData("doesnotexist", 0)]
    public void Search_finds_substrings_anywhere_in_events(string text, long expected)
    {
        using var f = new IndexFixture();
        var lines = File.ReadAllText(IndexFixture.Fixture("b3dweb-gateway/Gateway.log"))
            .Replace("SOPInstanceUID=2.25.299410238811740092734811920051234567 ignored", "SOPInstanceUID=2.25.840.11361.9 ignored (Ação)", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(f.LogDir, "Gateway.log"), lines);
        f.Scan(f.AddSource("Europe/Lisbon"));
        Assert.Equal(expected, f.Search.Search(new SearchRequest { Text = text }).MatchCount);
    }

    [Fact]
    public void Index_from_version_2_is_upgraded_to_substring_search_without_rescanning()
    {
        using var f = new IndexFixture();
        File.Copy(IndexFixture.Fixture("b3dweb-gateway/Gateway.log"), Path.Combine(f.LogDir, "Gateway.log"));
        f.Scan(f.AddSource("Europe/Lisbon"));
        using (var c = f.Db.Open())
        {
            // Recreate the version 2 word index.
            IndexDatabase.Exec(c, "DROP TRIGGER events_ai; DROP TRIGGER events_ad; DROP TABLE events_fts; " +
                "CREATE VIRTUAL TABLE events_fts USING fts5(message, detail, category, component, content='events', content_rowid='id', tokenize='unicode61 remove_diacritics 2'); " +
                "INSERT INTO events_fts(events_fts) VALUES('rebuild'); PRAGMA user_version=2;");
        }

        Assert.Equal(0, f.Search.Search(new SearchRequest { Text = "ACC000" }).MatchCount);
        f.Db.Initialise();
        Assert.Equal(5, f.Search.Search(new SearchRequest { Text = "ACC000" }).MatchCount);
        using var check = f.Db.Open();
        Assert.Equal(3L, IndexDatabase.Scalar<long>(check, "PRAGMA user_version"));
    }

    [Fact]
    public void Category_filter_narrows_a_text_search()
    {
        using var f = new IndexFixture();
        File.Copy(IndexFixture.Fixture("b3dweb-gateway/Gateway.log"), Path.Combine(f.LogDir, "Gateway.log"));
        f.Scan(f.AddSource("Europe/Lisbon"));
        Assert.Equal(4, f.Search.Search(new SearchRequest { Text = "SYNACC0001" }).MatchCount);
        Assert.Equal(2, f.Search.Search(new SearchRequest { Text = "SYNACC0001", Category = "SCU" }).MatchCount);
        Assert.Contains(new CategoryCount("SCU", 2, 1, 0), new OverviewService(f.Db).Categories());
    }

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

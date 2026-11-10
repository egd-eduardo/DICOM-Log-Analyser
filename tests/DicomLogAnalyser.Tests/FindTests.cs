using System.Text;
using DicomLogAnalyser.Core.Search;

namespace DicomLogAnalyser.Tests;

public class TextFoldingTests
{
    [Theory]
    [InlineData("ÉCOLE", "ecole")]
    [InlineData("Associação", "associacao")]
    [InlineData("Ünlü Şehir", "unlu sehir")]
    [InlineData("Đakovo", "dakovo")]
    [InlineData("KIRMIZI ı", "kirmizi i")]
    [InlineData("Άλφα Ωμέγα", "αλφα ωμεγα")]
    [InlineData("Šiauliai Įėjimas", "siauliai iejimas")]
    [InlineData("Győr Ő", "gyor o")]
    [InlineData("ACC000123", "acc000123")]
    public void Folds_case_and_accents(string input, string expected)
    {
        Assert.Equal(expected, TextFolding.Fold(input));
        Assert.Equal(input.Length, TextFolding.Fold(input).Length);
    }
}

public class FileFinderTests
{
    private static FindTarget Target(string path, string rel) => new(1, "Gateway", rel, path);

    [Fact]
    public void Parses_words_phrases_and_exclusions()
    {
        var (include, exclude) = FileFinder.Parse("ACC000123  \"Study  UID\" -Échec - \"open");
        Assert.Equal(["acc000123", "study  uid", "-", "open"], include);
        Assert.Equal(["echec"], exclude);
        Assert.Empty(FileFinder.Parse("   ").Include);
        Assert.Empty(FileFinder.Parse(null).Include);
    }

    [Fact]
    public void Finds_lines_ignoring_case_and_accents_with_context()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "Gateway.log");
        var lines = Enumerable.Range(1, 20).Select(i => $"INF 2026.11.03 10:00:{i:00} SCU - routine line {i}").ToList();
        lines[9] = "INF 2026.11.03 10:00:10 SCP - C-STORE accessionNumber=ACC000123 patientID=SYN-PAT-0001 Associação aceite";
        lines[10] = "INF 2026.11.03 10:00:11 SCP - studyInstanceUID=2.25.271828182845904523536028747135266249";
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.UTF8);

        var r = FileFinder.Find([Target(path, "Gateway.log")], "acc000123 ASSOCIACAO", 100, TimeSpan.FromMinutes(1), CancellationToken.None);
        var hit = Assert.Single(r.Hits);
        Assert.Equal(10, hit.Line);
        Assert.Equal(["INF 2026.11.03 10:00:08 SCU - routine line 8", "INF 2026.11.03 10:00:09 SCU - routine line 9"], hit.Before);
        Assert.Equal(2, hit.After.Count);
        Assert.Contains("studyInstanceUID", hit.After[0], StringComparison.Ordinal);
        Assert.Equal(1, r.FilesSearched);
        Assert.Equal(new FileInfo(path).Length, r.BytesRead);
        Assert.False(r.Truncated);

        // Exclusion and no match.
        Assert.Empty(FileFinder.Find([Target(path, "Gateway.log")], "ACC000123 -aceite", 100, TimeSpan.FromMinutes(1), CancellationToken.None).Hits);
        Assert.Single(FileFinder.Find([Target(path, "Gateway.log")], "\"syn-pat-0001 associação\"", 100, TimeSpan.FromMinutes(1), CancellationToken.None).Hits);
    }

    [Fact]
    public void Latin1_files_are_found_with_unaccented_text()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "old.log");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes("INF 2026.11.03 10:00:00 SCP - Rejeição pelo destino\n"));
        Assert.Single(FileFinder.Find([Target(path, "old.log")], "rejeicao", 10, TimeSpan.FromMinutes(1), CancellationToken.None).Hits);
    }

    [Fact]
    public void Caps_hits_reports_unreadable_files_and_honours_cancellation()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "many.log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 5000).Select(i => $"INF line {i} C-FIND\n")));
        var missing = Path.Combine(t.Path, "gone.log");

        var r = FileFinder.Find([Target(missing, "gone.log"), Target(path, "many.log")], "c-find", 50, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(50, r.Hits.Count);
        Assert.True(r.Truncated);
        Assert.Equal(["Gateway: gone.log"], r.Unreadable);
        // The last hit still gets its lines after.
        Assert.Equal(2, r.Hits[^1].After.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FileFinder.Find([Target(path, "many.log")], "c-find", 50, TimeSpan.FromMinutes(1), cts.Token));
    }

    [Fact]
    public void Context_at_file_boundaries_and_overlapping_hits()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "short.log");
        File.WriteAllText(path, "match one\nmatch two\nlast");
        var r = FileFinder.Find([Target(path, "short.log")], "MATCH", 10, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(2, r.Hits.Count);
        Assert.Empty(r.Hits[0].Before);
        Assert.Equal(["match two", "last"], r.Hits[0].After);
        Assert.Equal(["match one"], r.Hits[1].Before);
        Assert.Equal(["last"], r.Hits[1].After);
    }

    [Fact]
    public void Is_fast_enough_for_large_logs()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "big.log");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 200_000; i++) w.Write($"INF 2026.11.03 10:00:00.{i % 1000:000} SCU [Assoc {i}] - C-STORE-RQ sent, SOP Instance UID 2.25.{i}0000000000000000000000001\n");
        }

        var r = FileFinder.Find([Target(path, "big.log")], "assoc 199999]", 100, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Single(r.Hits);
        Assert.True(r.Seconds < 20, $"took {r.Seconds} s");
    }
}

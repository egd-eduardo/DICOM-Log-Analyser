using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Tests;

public class IngestorTests
{
    private const string L1 = "INF 2026.11.03 09:40:29 SPS - first Accession=SYNACC0001\n";
    private const string L2 = "ERROR 2026.11.03 09:41:02 SCU - failed Accession=SYNACC0001\n";
    private const string Trace = "System.IO.IOException: boom\n   at X.Y()\n";
    private const string L3 = "INF 2026.11.03 09:45:00 SCH - retry\n";

    [Fact]
    public void Indexes_both_fixture_formats_with_traceability()
    {
        using var f = new IndexFixture();
        File.Copy(IndexFixture.Fixture("b3dweb-gateway/Gateway.log"), Path.Combine(f.LogDir, "Gateway.log"));
        File.Copy(IndexFixture.Fixture("dicom-router/DicomRouter20261027.log"), Path.Combine(f.LogDir, "DicomRouter20261027.log"));
        var s = f.AddSource("Europe/Lisbon");
        var summary = f.Scan(s);
        Assert.Equal("ok", summary.Status);
        Assert.Equal(2, summary.FilesChanged);
        Assert.Equal(10 + 6, f.Count());
        Assert.Equal(0, f.Count("kind=1"));
        Assert.Equal(2, f.Count("level>=5 AND has_exception=1"));

        var res = f.Search.Search(new SearchRequest { Text = "SYNACC0001", MinLevel = EventLevel.Error });
        var hit = Assert.Single(res.Items);
        Assert.Equal("Gateway.log", hit.File);
        Assert.Equal(4, hit.LineStart);
        var detail = f.Search.Get(hit.Id)!;
        Assert.Equal(7, detail.Event.LineEnd);
        Assert.Equal("verified", detail.EvidenceStatus);
        Assert.StartsWith("ERROR 2026.11.03 09:41:02 SCU", detail.OriginalText, StringComparison.Ordinal);
        Assert.Contains("StoreScu.cs:line 88", detail.OriginalText, StringComparison.Ordinal);
    }

    [Fact]
    public void Incremental_scan_reads_only_appended_data_and_completes_open_records()
    {
        using var f = new IndexFixture();
        var file = Path.Combine(f.LogDir, "g.log");
        File.WriteAllText(file, L1 + L2 + "System.IO.IOException: bo");
        var s = f.AddSource();
        f.Scan(s);
        Assert.Equal(2, f.Count());

        // The trace continues and a new record arrives: the open record is re-parsed, nothing is duplicated.
        File.AppendAllText(file, "om\n   at X.Y()\n" + L3);
        var second = f.Scan(s);
        Assert.Equal(3, f.Count());
        Assert.True(second.BytesRead < new FileInfo(file).Length);
        var err = f.Search.Search(new SearchRequest { MinLevel = EventLevel.Error }).Items.Single();
        Assert.True(err.HasException);
        Assert.Contains("boom", f.Search.Get(err.Id)!.Detail, StringComparison.Ordinal);

        // Unchanged file: nothing read.
        var third = f.Scan(s);
        Assert.Equal(0, third.FilesChanged);
        Assert.Equal(3, f.Count());
    }

    [Fact]
    public void Truncated_file_is_reindexed_from_the_start()
    {
        using var f = new IndexFixture();
        var file = Path.Combine(f.LogDir, "g.log");
        File.WriteAllText(file, L1 + L2 + Trace + L3);
        var s = f.AddSource();
        f.Scan(s);
        Assert.Equal(3, f.Count());
        File.WriteAllText(file, L3);
        var summary = f.Scan(s);
        Assert.Equal(1, summary.FilesReset);
        Assert.Equal(1, f.Count());
    }

    [Fact]
    public void Replaced_file_with_different_start_is_reindexed()
    {
        using var f = new IndexFixture();
        var file = Path.Combine(f.LogDir, "g.log");
        File.WriteAllText(file, L1 + L2);
        var s = f.AddSource();
        f.Scan(s);
        File.WriteAllText(file, L3 + L3 + L3 + L3);
        var summary = f.Scan(s);
        Assert.Equal(1, summary.FilesReset);
        Assert.Equal(4, f.Count());
        Assert.Equal(0, f.Count("message LIKE '%SYNACC%'"));
    }

    [Fact]
    public void Rotation_by_rename_keeps_the_index_and_new_file_is_added()
    {
        using var f = new IndexFixture();
        var file = Path.Combine(f.LogDir, "app.log");
        File.WriteAllText(file, L1 + L2 + Trace);
        var s = f.AddSource();
        f.Scan(s);
        File.Move(file, Path.Combine(f.LogDir, "app.log.1"));
        File.WriteAllText(file, L3);
        var summary = f.Scan(s);
        Assert.Equal(1, summary.FilesRenamed);
        Assert.Equal(0, summary.FilesMissing);
        Assert.Equal(3, f.Count());
        Assert.Equal(2, f.Count("file_id=(SELECT id FROM files WHERE rel_path='app.log.1')"));
    }

    [Fact]
    public void Deleted_file_is_reported_missing_and_its_events_are_kept()
    {
        using var f = new IndexFixture();
        var file = Path.Combine(f.LogDir, "g.log");
        File.WriteAllText(file, L1 + L2);
        var s = f.AddSource();
        f.Scan(s);
        File.Delete(file);
        var summary = f.Scan(s);
        Assert.Equal(1, summary.FilesMissing);
        Assert.Equal(2, f.Count());
        using var c = f.Db.Open();
        Assert.Equal("missing", IndexDatabase.Scalar<string>(c, "SELECT status FROM files"));
    }

    [Fact]
    public void Unrecognised_and_binary_files_are_reported_not_indexed()
    {
        using var f = new IndexFixture();
        File.WriteAllText(Path.Combine(f.LogDir, "notes.txt"), "just some text\nnothing to see\n");
        File.WriteAllBytes(Path.Combine(f.LogDir, "blob.bin"), [0, 1, 2, 0, 65]);
        File.WriteAllText(Path.Combine(f.LogDir, "archive.zip"), "PK");
        var s = f.AddSource();
        var summary = f.Scan(s);
        Assert.Equal(2, summary.FilesUnrecognised);
        Assert.Equal("warnings", summary.Status);
        Assert.Equal(0, f.Count());
    }

    [Fact]
    public void Unreachable_source_keeps_events_and_reports_status()
    {
        using var f = new IndexFixture();
        File.WriteAllText(Path.Combine(f.LogDir, "g.log"), L1);
        var s = f.AddSource();
        f.Scan(s);
        Directory.Move(f.LogDir, f.LogDir + "-offline");
        var summary = f.Scan(s);
        Assert.Equal("unreachable", summary.Status);
        Assert.Equal(1, f.Count());
        var notes = f.Search.Search(new SearchRequest { Text = "nothing-matches-this" }).CoverageNotes;
        Assert.Contains(notes, n => n.Contains("unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_before_first_header_are_kept_as_unparsed()
    {
        using var f = new IndexFixture();
        File.WriteAllText(Path.Combine(f.LogDir, "g.log"), "continued from previous file\n   at A.B()\n" + L1 + L1 + L1);
        var s = f.AddSource();
        f.Scan(s);
        Assert.Equal(1, f.Count("kind=1"));
        using var c = f.Db.Open();
        Assert.Equal(2, IndexDatabase.Scalar<long>(c, "SELECT unparsed_lines FROM files"));
    }

    [Fact]
    public void Source_files_are_never_modified()
    {
        using var f = new IndexFixture();
        var file = Path.Combine(f.LogDir, "g.log");
        File.WriteAllText(file, L1 + L2 + Trace + L3);
        var before = File.ReadAllBytes(file);
        var mtime = File.GetLastWriteTimeUtc(file);
        var s = f.AddSource();
        f.Scan(s);
        f.Scan(s);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Search_paginates_without_duplicates()
    {
        using var f = new IndexFixture();
        var lines = string.Concat(Enumerable.Range(0, 250).Select(i => $"INF 2026.11.03 10:{i / 60:00}:{i % 60:00} SPS - item {i} SYNACC9\n"));
        File.WriteAllText(Path.Combine(f.LogDir, "g.log"), lines);
        var s = f.AddSource();
        f.Scan(s);
        var seen = new HashSet<long>();
        string? cursor = null;
        do
        {
            var page = f.Search.Search(new SearchRequest { Text = "synacc9", Limit = 100, Cursor = cursor });
            foreach (var e in page.Items) Assert.True(seen.Add(e.Id));
            cursor = page.NextCursor;
            Assert.Equal(250, page.MatchCount);
        }
        while (cursor is not null);
        Assert.Equal(250, seen.Count);
    }
}

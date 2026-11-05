using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Sources;

namespace DicomLogAnalyser.Tests;

/// <summary>At most 5 calendar days of files (by last-modified date) are analysed per folder; bundles are not limited.</summary>
public class SelectionTests
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    private static CandidateFile F(string name, int day) => new(name, 1, new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 11, day));

    /// <summary>Writes one log file per day, 1 to <paramref name="days"/> November 2026, with that day as modification date.</summary>
    private static void WriteDailyFiles(string dir, int days)
    {
        for (var d = 1; d <= days; d++)
        {
            var path = Path.Combine(dir, $"gateway-{d:00}.log");
            File.WriteAllText(path, $"INF 2026.11.{d:00} 10:00:00 SPS - day {d}\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, d, 12, 0, 0, DateTimeKind.Utc));
        }
    }

    [Fact]
    public void Default_choice_is_the_latest_five_calendar_days()
    {
        var files = Enumerable.Range(1, 8).Select(d => F($"f{d}", d)).ToList();
        Assert.Equal(["f4", "f5", "f6", "f7", "f8"], FileSelection.Default(files).Order().ToArray());
        Assert.Equal(5, FileSelection.SpanDays(files.Where(f => f.RelPath is "f4" or "f8")));
        Assert.Empty(FileSelection.Default([]));
    }

    [Fact]
    public void Validation_enforces_the_limit_and_known_files()
    {
        var files = Enumerable.Range(1, 8).Select(d => F($"f{d}", d)).ToList();
        Assert.Null(FileSelection.Validate(["f2", "f6"], files));
        Assert.Contains("6 calendar days", FileSelection.Validate(["f2", "f7"], files), StringComparison.Ordinal);
        Assert.NotNull(FileSelection.Validate([], files));
        Assert.NotNull(FileSelection.Validate(null, files));
        Assert.NotNull(FileSelection.Validate(["missing.log"], files));
    }

    [Fact]
    public void Calendar_days_are_counted_in_the_source_time_zone()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "a.log");
        File.WriteAllText(path, "x");
        // 23:30 UTC on 31 October is 23:30 in Lisbon (winter) but already 1 November in Tokyo.
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2026, 10, 31), FileSelection.Candidate(t.Path, new FileInfo(path), Lisbon).ModifiedDate);
        Assert.Equal(new DateOnly(2026, 11, 1), FileSelection.Candidate(t.Path, new FileInfo(path), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")).ModifiedDate);
    }

    [Fact]
    public void Folder_scan_analyses_only_the_latest_five_days_and_saves_the_choice()
    {
        using var f = new IndexFixture();
        WriteDailyFiles(f.LogDir, 8);
        var s = f.AddSource("Europe/Lisbon");
        var summary = f.Scan(s);
        Assert.Equal(5, f.Count());
        Assert.Equal(3, summary.FilesNotSelected);
        Assert.Equal(0, f.Count("message='day 1' OR message='day 3'"));
        var saved = f.Sources.Get(s.Id)!;
        Assert.Equal(5, saved.SelectedFiles!.Count);
        Assert.Equal(3, saved.UnselectedFiles);
        Assert.Equal(0, saved.NewFiles);
    }

    [Fact]
    public void Unticking_a_file_removes_its_events_and_new_files_wait_for_a_choice()
    {
        using var f = new IndexFixture();
        WriteDailyFiles(f.LogDir, 5);
        var s = f.AddSource("Europe/Lisbon");
        f.Scan(s);
        Assert.Equal(5, f.Count());

        f.Sources.SetSelection(s.Id, ["gateway-04.log", "gateway-05.log"]);
        f.Scan(s);
        Assert.Equal(2, f.Count());
        Assert.Equal(2L, Scalar(f, "SELECT count(*) FROM files"));

        // An unticked file that keeps being written to is not "new".
        File.SetLastWriteTimeUtc(Path.Combine(f.LogDir, "gateway-01.log"), DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, f.Scan(s).FilesNew);

        // A file appearing after the choice is reported as new and not analysed.
        var newer = Path.Combine(f.LogDir, "gateway-new.log");
        File.WriteAllText(newer, "INF 2026.11.06 10:00:00 SPS - new day\n");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(5));
        var summary = f.Scan(s);
        Assert.Equal(2, f.Count());
        Assert.Equal(1, summary.FilesNew);
        Assert.Contains(f.Search.Search(new SearchRequest { Text = "x" }).CoverageNotes, n => n.Contains("new file(s) that are not chosen", StringComparison.Ordinal));
    }

    [Fact]
    public void Rotation_keeps_the_chosen_name_and_the_renamed_content()
    {
        using var f = new IndexFixture();
        var log = Path.Combine(f.LogDir, "app.log");
        File.WriteAllText(log, "INF 2026.11.03 10:00:00 SPS - before rotation\nINF 2026.11.03 10:00:01 SPS - second\n");
        var s = f.AddSource("Europe/Lisbon");
        f.Scan(s);
        File.Move(log, Path.Combine(f.LogDir, "app.log.1"));
        File.WriteAllText(log, "INF 2026.11.03 11:00:00 SPS - after rotation\n");
        f.Scan(s);
        Assert.Equal(3, f.Count());
        Assert.Equal(["app.log", "app.log.1"], f.Sources.Get(s.Id)!.SelectedFiles!.ToArray());
    }

    [Fact]
    public void Bundles_are_indexed_completely()
    {
        using var f = new IndexFixture();
        WriteDailyFiles(f.LogDir, 8);
        var id = f.Sources.Add(new SourceDefinition { Label = "Bundle", Kind = SourceKinds.Bundle, Path = f.LogDir, TimeZone = "Europe/Lisbon" });
        var summary = f.Ingestor.Scan(f.Sources.Get(id)!, null, CancellationToken.None);
        Assert.Equal(8, f.Count());
        Assert.Equal(0, summary.FilesNotSelected);
        Assert.Null(f.Sources.Get(id)!.SelectedFiles);
    }

    [Fact]
    public void Index_from_the_previous_version_is_upgraded()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "index.db");
        var db = new IndexDatabase(file);
        db.Initialise();
        using (var c = db.Open())
        {
            IndexDatabase.Exec(c, "ALTER TABLE sources DROP COLUMN selected_files; ALTER TABLE sources DROP COLUMN selection_utc; ALTER TABLE sources DROP COLUMN selection_seen; " +
                "ALTER TABLE sources DROP COLUMN unselected_files; ALTER TABLE sources DROP COLUMN new_files; PRAGMA user_version=1;");
        }

        db.Initialise();
        var repo = new SourceRepository(db, TimeProvider.System);
        var id = repo.Add(new SourceDefinition { Label = "x", Kind = SourceKinds.Folder, Path = t.Path });
        repo.SetSelection(id, ["a.log"]);
        Assert.Equal(["a.log"], repo.Get(id)!.SelectedFiles!.ToArray());
        using var check = db.Open();
        Assert.Equal(IndexDatabase.SchemaVersion, IndexDatabase.Scalar<long>(check, "PRAGMA user_version"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static long Scalar(IndexFixture f, string sql)
    {
        using var c = f.Db.Open();
        return IndexDatabase.Scalar<long>(c, sql);
    }
}

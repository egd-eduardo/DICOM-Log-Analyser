using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Core.Storage;

namespace DicomLogAnalyser.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dla-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Path).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A data folder with an initialised index and a source folder for logs.</summary>
public sealed class IndexFixture : IDisposable
{
    public IndexFixture()
    {
        Paths = new AppPaths(Temp.Sub("data"));
        Paths.EnsureCreated();
        Db = new IndexDatabase(Paths.DatabaseFile);
        Db.Initialise();
        Sources = new SourceRepository(Db, TimeProvider.System);
        Ingestor = new Ingestor(Db, Sources, TimeProvider.System);
        Search = new SearchService(Db);
        LogDir = Temp.Sub("logs");
    }

    public TempDir Temp { get; } = new();
    public AppPaths Paths { get; }
    public IndexDatabase Db { get; }
    public SourceRepository Sources { get; }
    public Ingestor Ingestor { get; }
    public SearchService Search { get; }
    public string LogDir { get; }

    public SourceDefinition AddSource(string? timeZone = null, string label = "Test source")
    {
        var id = Sources.Add(new SourceDefinition { Label = label, Kind = SourceKinds.Folder, Path = LogDir, TimeZone = timeZone });
        return Sources.Get(id)!;
    }

    public ScanSummary Scan(SourceDefinition s) => Ingestor.Scan(Sources.Get(s.Id)!, null, CancellationToken.None);

    public long Count(string where = "1=1")
    {
        using var c = Db.Open();
        return IndexDatabase.Scalar<long>(c, "SELECT count(*) FROM events WHERE " + where);
    }

    public static string Fixture(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", relative);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Temp.Dispose();
    }
}

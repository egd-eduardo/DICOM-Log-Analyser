using System.IO.Compression;
using System.Text;
using DicomLogAnalyser.Core.Bundles;
using DicomLogAnalyser.Core.Storage;

namespace DicomLogAnalyser.Tests;

public class BundleImporterTests
{
    private static (TempDir Temp, AppPaths Paths, BundleImporter Importer) Setup(BundleLimits? limits = null)
    {
        var t = new TempDir();
        var paths = new AppPaths(t.Sub("data"));
        paths.EnsureCreated();
        return (t, paths, new BundleImporter(paths, limits ?? new BundleLimits()));
    }

    private static MemoryStream Zip(Action<ZipArchive> build)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) build(zip);
        ms.Position = 0;
        return ms;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), Encoding.UTF8);
        w.Write(content);
    }

    [Fact]
    public async Task Imports_log_files_skips_others_and_makes_them_read_only()
    {
        var (t, paths, importer) = Setup();
        using var _ = t;
        using var zip = Zip(z =>
        {
            Add(z, "Gateway/0/Gateway.log", "INF 2026.11.03 09:40:29 SPS - x\n");
            Add(z, "Router/DicomRouter20261027.log.1", "x\n");
            Add(z, "notes.docx", "x");
            Add(z, "nested.zip", "x");
        });
        var r = await importer.ImportAsync(zip, "bundle.zip", CancellationToken.None);
        Assert.Equal(2, r.FilesExtracted);
        Assert.Equal(2, r.Skipped.Count);
        Assert.True(File.Exists(Path.Combine(r.FilesDirectory, "Gateway", "0", "Gateway.log")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TempDirectory));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Path.Combine(r.FilesDirectory, "Gateway", "0", "Gateway.log")));
        Assert.True(importer.Delete(r.FilesDirectory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(r.FilesDirectory)));
    }

    [Theory]
    [InlineData("../evil.log")]
    [InlineData("a/../../evil.log")]
    [InlineData("/etc/evil.log")]
    [InlineData("C:/evil.log")]
    [InlineData("..\\evil.log")]
    [InlineData("a/con:x.log")]
    public async Task Rejects_path_traversal_and_absolute_paths(string name)
    {
        var (t, paths, importer) = Setup();
        using var _ = t;
        using var zip = Zip(z => Add(z, name, "x\n"));
        await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(zip, "b.zip", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TempDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.BundlesDirectory));
    }

    [Fact]
    public async Task Rejects_symbolic_link_entries()
    {
        var (t, _, importer) = Setup();
        using var __ = t;
        using var zip = Zip(z =>
        {
            var e = z.CreateEntry("link.log");
            e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var w = new StreamWriter(e.Open());
            w.Write("/etc/passwd");
        });
        var ex = await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(zip, "b.zip", CancellationToken.None));
        Assert.Contains("symbolic link", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_decompression_bomb_by_actual_size()
    {
        var (t, _, importer) = Setup(new BundleLimits { MaxTotalBytes = 5 * 1024 * 1024 });
        using var __ = t;
        using var zip = Zip(z =>
        {
            var e = z.CreateEntry("big.log", CompressionLevel.SmallestSize);
            using var s = e.Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 20; i++) s.Write(block);
        });
        await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(zip, "b.zip", CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_high_compression_ratio()
    {
        var (t, _, importer) = Setup(new BundleLimits { MaxCompressionRatio = 50 });
        using var __ = t;
        using var zip = Zip(z =>
        {
            var e = z.CreateEntry("big.log", CompressionLevel.SmallestSize);
            using var s = e.Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 20; i++) s.Write(block);
        });
        var ex = await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(zip, "b.zip", CancellationToken.None));
        Assert.Contains("compression ratio", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_oversized_upload_while_streaming()
    {
        var (t, paths, importer) = Setup(new BundleLimits { MaxUploadBytes = 1000 });
        using var __ = t;
        using var big = new MemoryStream(new byte[5000]);
        await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(big, "x.log", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TempDirectory));
    }

    [Fact]
    public async Task Rejects_binary_and_utf16_files()
    {
        var (t, _, importer) = Setup();
        using var __ = t;
        using var bin = new MemoryStream([0x4D, 0x5A, 0x00, 0x01]);
        await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(bin, "x.exe", CancellationToken.None));
        using var utf16 = new MemoryStream([0xFF, 0xFE, 0x41, 0x00]);
        await Assert.ThrowsAsync<BundleRejectedException>(() => importer.ImportAsync(utf16, "x.log", CancellationToken.None));
    }

    [Fact]
    public async Task Imports_single_text_and_gzip_files_with_sanitised_names()
    {
        var (t, _, importer) = Setup();
        using var __ = t;
        using var text = new MemoryStream("INF 2026.11.03 09:40:29 SPS - x\n"u8.ToArray());
        var r1 = await importer.ImportAsync(text, "../../Gateway 0.log", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(r1.FilesDirectory, "Gateway0.log")));

        var gz = new MemoryStream();
        using (var g = new GZipStream(gz, CompressionMode.Compress, leaveOpen: true)) g.Write("INF 2026.11.03 09:40:29 SPS - x\n"u8);
        gz.Position = 0;
        var r2 = await importer.ImportAsync(gz, "router.log.gz", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(r2.FilesDirectory, "router.log")));
    }

    [Fact]
    public void Delete_refuses_paths_outside_the_bundle_store()
    {
        var (t, _, importer) = Setup();
        using var __ = t;
        var outside = t.Sub("elsewhere/files");
        Assert.False(importer.Delete(outside));
        Assert.True(Directory.Exists(outside));
    }
}

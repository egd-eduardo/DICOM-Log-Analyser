using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Storage;

namespace DicomLogAnalyser.Core.Bundles;

public sealed class BundleLimits
{
    public long MaxUploadBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxEntryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 20_000;
    public int MaxCompressionRatio { get; init; } = 200;
    public int MaxPathLength { get; init; } = 400;
    public int MaxPathDepth { get; init; } = 16;
}

public sealed class BundleRejectedException(string message) : Exception(message)
{
    public BundleRejectedException() : this("The bundle was rejected.")
    {
    }

    public BundleRejectedException(string message, Exception inner) : this(message) => _ = inner;
}

public sealed record BundleResult(string BundleId, string FilesDirectory, string Sha256, long UploadBytes, int FilesExtracted, long BytesExtracted,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Imports a log bundle (a .zip archive, a single .gz file, or a single plain-text log) into managed storage.
/// The upload is streamed to a temporary file, never held in memory. Archive entries are validated against path
/// traversal, absolute paths, links, encryption, nesting, oversize and decompression bombs; sizes are enforced on the
/// bytes actually written, not on the sizes the archive declares. On any failure nothing is kept.
/// </summary>
public sealed partial class BundleImporter(AppPaths paths, BundleLimits limits)
{
    [GeneratedRegex(@"(\.log|\.txt|\.out|\.log\.\d{1,4}|\.\d{1,4}|\.log\.\d{4}-?\d{2}-?\d{2}|\.log\.\d{8}\.\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedName();

    [GeneratedRegex(@"^[^\x00-\x1f<>:""|?*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegment();

    public BundleLimits Limits => limits;

    public async Task<BundleResult> ImportAsync(Stream upload, string? originalName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        Directory.CreateDirectory(paths.TempDirectory);
        var id = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var tempFile = Path.Combine(paths.TempDirectory, "upload-" + id + ".part");
        var staging = Path.Combine(paths.TempDirectory, "extract-" + id);
        var finalDir = Path.Combine(paths.BundlesDirectory, id);
        try
        {
            var (sha, size) = await SaveUploadAsync(upload, tempFile, ct).ConfigureAwait(false);
            if (size == 0) throw new BundleRejectedException("The uploaded file is empty.");
            Directory.CreateDirectory(staging);
            var skipped = new List<string>();
            int files;
            long bytes;
            var kind = await DetectAsync(tempFile, ct).ConfigureAwait(false);
            switch (kind)
            {
                case "zip":
                    (files, bytes) = ExtractZip(tempFile, staging, skipped, ct);
                    break;
                case "gzip":
                    (files, bytes) = await ExtractGzipAsync(tempFile, staging, originalName, ct).ConfigureAwait(false);
                    break;
                case "text":
                    (files, bytes) = CopyText(tempFile, staging, originalName, size);
                    break;
                default:
                    throw new BundleRejectedException("Unsupported file type. Upload a .zip archive, a .gz file or a plain-text log (.log/.txt).");
            }

            if (files == 0) throw new BundleRejectedException("The bundle contains no supported log files (.log, .txt, .out or rotated names such as .log.1).");
            Directory.CreateDirectory(paths.BundlesDirectory);
            Directory.CreateDirectory(finalDir);
            var filesDir = Path.Combine(finalDir, "files");
            Directory.Move(staging, filesDir);
            MakeReadOnly(filesDir);
            return new BundleResult(id, filesDir, sha, size, files, bytes, skipped);
        }
        catch
        {
            TryDelete(staging);
            TryDelete(finalDir);
            throw;
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    /// <summary>Deletes an imported bundle's files. Only directories directly under the bundles folder are accepted.</summary>
    public bool Delete(string filesDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(filesDirectory);
        var bundleDir = Path.GetDirectoryName(Path.GetFullPath(filesDirectory));
        if (bundleDir is null || !string.Equals(Path.GetDirectoryName(bundleDir), Path.GetFullPath(paths.BundlesDirectory), StringComparison.Ordinal)) return false;
        if (!Directory.Exists(bundleDir)) return true;
        foreach (var f in new DirectoryInfo(bundleDir).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
        Directory.Delete(bundleDir, recursive: true);
        return true;
    }

    private async Task<(string Sha, long Size)> SaveUploadAsync(Stream upload, string tempFile, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        var output = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous);
        await using (output.ConfigureAwait(false))
        {
            int read;
            while ((read = await upload.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > limits.MaxUploadBytes) throw new BundleRejectedException($"The upload exceeds the limit of {limits.MaxUploadBytes / (1024 * 1024)} MB.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
    }

    private static async Task<string> DetectAsync(string file, CancellationToken ct)
    {
        var head = new byte[4096];
        int n;
        var s = File.OpenRead(file);
        await using (s.ConfigureAwait(false))
        {
            n = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        }

        var span = head.AsSpan(0, n);
        if (span.StartsWith("PK\x03\x04"u8) || span.StartsWith("PK\x05\x06"u8)) return "zip";
        if (span.StartsWith(new byte[] { 0x1F, 0x8B })) return "gzip";
        if (span.Length >= 2 && ((span[0] == 0xFF && span[1] == 0xFE) || (span[0] == 0xFE && span[1] == 0xFF))) return "unsupported";
        return span.Contains((byte)0) ? "unsupported" : "text";
    }

    private (int Files, long Bytes) ExtractZip(string zipFile, string staging, List<string> skipped, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipFile);
        if (archive.Entries.Count > limits.MaxEntries) throw new BundleRejectedException($"The archive has more than {limits.MaxEntries} entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = NormaliseEntryName(entry.FullName);
            if (rel is null) throw new BundleRejectedException("The archive contains an unsafe path (absolute path, drive letter, '..' or invalid characters). Nothing was imported.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                if (entry.Length != 0) throw new BundleRejectedException("The archive contains a malformed folder entry.");
                continue;
            }

            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is 0xA000) throw new BundleRejectedException("The archive contains a symbolic link. Nothing was imported.");
            if (unixType is not (0 or 0x8000)) throw new BundleRejectedException("The archive contains a special file entry. Nothing was imported.");
            if (entry.IsEncrypted) throw new BundleRejectedException("Encrypted archives are not supported. Nothing was imported.");
            if (!AllowedName().IsMatch(rel))
            {
                if (skipped.Count < 200) skipped.Add(rel);
                continue;
            }

            if (!names.Add(rel)) throw new BundleRejectedException("The archive contains duplicate file names (differing only by case).");
            if (entry.Length > limits.MaxEntryBytes) throw new BundleRejectedException("A file in the archive exceeds the per-file size limit.");
            if (entry.CompressedLength > 0 && entry.Length > 10 * 1024 * 1024 && entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                throw new BundleRejectedException("A file in the archive has a suspicious compression ratio (possible decompression bomb).");

            var target = Path.GetFullPath(Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new BundleRejectedException("The archive contains an unsafe path. Nothing was imported.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            var written = CopyLimited(input, target, Math.Min(limits.MaxEntryBytes, limits.MaxTotalBytes - total), entry.CompressedLength, ct);
            total += written;
            files++;
        }

        return (files, total);
    }

    private async Task<(int, long)> ExtractGzipAsync(string file, string staging, string? originalName, CancellationToken ct)
    {
        var name = SafeFileName(originalName);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        if (!AllowedName().IsMatch(name)) name += ".log";
        var compressedSize = new FileInfo(file).Length;
        var raw = File.OpenRead(file);
        await using (raw.ConfigureAwait(false))
        {
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            var written = CopyLimited(gz, Path.Combine(staging, name), Math.Min(limits.MaxEntryBytes, limits.MaxTotalBytes), compressedSize, ct);
            return (1, written);
        }
    }

    private static (int, long) CopyText(string file, string staging, string? originalName, long size)
    {
        var name = SafeFileName(originalName);
        if (!AllowedName().IsMatch(name)) name += ".log";
        File.Copy(file, Path.Combine(staging, name));
        return (1, size);
    }

    private long CopyLimited(Stream input, string target, long maxBytes, long compressedLength, CancellationToken ct)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            written += read;
            if (written > maxBytes) throw new BundleRejectedException("The bundle expands beyond the allowed size (possible decompression bomb). Nothing was imported.");
            if (compressedLength > 0 && written > 10 * 1024 * 1024 && written / compressedLength > limits.MaxCompressionRatio)
                throw new BundleRejectedException("A file in the bundle has a suspicious compression ratio (possible decompression bomb). Nothing was imported.");
            output.Write(buffer, 0, read);
        }

        return written;
    }

    internal string? NormaliseEntryName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || fullName.Length > limits.MaxPathLength) return null;
        var name = fullName.Replace('\\', '/');
        if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':')) return null;
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > limits.MaxPathDepth) return null;
        foreach (var seg in segments)
        {
            if (seg is "." or ".." || seg.EndsWith('.') || seg.EndsWith(' ') || !SafeSegment().IsMatch(seg)) return null;
        }

        return string.Join('/', segments);
    }

    internal static string SafeFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "");
        n = new string(n.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray()).Trim('.');
        if (n.Length == 0) n = "upload.log";
        return n.Length > 120 ? n[^120..] : n;
    }

    private static void MakeReadOnly(string dir)
    {
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (OperatingSystem.IsWindows()) f.Attributes |= FileAttributes.ReadOnly;
            else File.SetUnixFileMode(f.FullName, UnixFileMode.UserRead);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

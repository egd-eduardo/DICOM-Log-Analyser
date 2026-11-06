namespace DicomLogAnalyser.Core.Sources;

/// <summary>Recognises folders on network shares (SMB/CIFS, NFS, AFP, WebDAV), on Windows, macOS and Linux.</summary>
public static class NetworkVolumes
{
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }

            var full = Path.GetFullPath(path);
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                var mount = d.RootDirectory.FullName;
                if (!PathValidator.IsSameOrInside(full, mount)) continue;
                if (best is null || mount.Length > best.RootDirectory.FullName.Length) best = d;
            }

            return best?.DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

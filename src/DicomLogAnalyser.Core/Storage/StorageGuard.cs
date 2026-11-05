namespace DicomLogAnalyser.Core.Storage;

/// <summary>Checks that the index is stored on a local disk, not on a network share.</summary>
public static class StorageGuard
{
    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs", "nfs4", "cifs", "smb", "smb2", "smb3", "smbfs", "afpfs", "webdav", "fuse.sshfs", "9p", "ncpfs", "davfs", "fuse.davfs2", "fuse.rclone",
    };

    /// <summary>Returns a problem description when <paramref name="path"/> is on a network location, otherwise null.</summary>
    public static string? NetworkLocationProblem(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return "The data folder is a network (UNC) path. The index database must be on a local disk.";
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            return "The data folder is a network (UNC) path. The index database must be on a local disk.";
        try
        {
            var drive = new DriveInfo(full);
            if (drive.DriveType == DriveType.Network) return "The data folder is on a network drive. The index database must be on a local disk.";
            if (!OperatingSystem.IsWindows() && NetworkFileSystems.Contains(drive.DriveFormat))
                return "The data folder is on a network file system. The index database must be on a local disk.";
        }
        catch (ArgumentException)
        {
            // Drive information is not available for this path; nothing more can be checked.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    public static bool IsNetworkFileSystem(string? format) => format is not null && NetworkFileSystems.Contains(format);
}

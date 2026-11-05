namespace DicomLogAnalyser.Core.Sources;

/// <summary>Linux mount information (used inside containers to confirm that source volumes are read-only).</summary>
public sealed record MountInfo(string MountPoint, string FileSystem, bool ReadOnly)
{
    private const string MountInfoFile = "/proc/self/mountinfo";

    /// <summary>Returns the mount that contains <paramref name="path"/>, or null when not on Linux.</summary>
    public static MountInfo? Find(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(MountInfoFile)) return null;
        try
        {
            return FindIn(File.ReadLines(MountInfoFile), path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static MountInfo? FindIn(IEnumerable<string> lines, string path)
    {
        MountInfo? best = null;
        foreach (var line in lines)
        {
            // id parent major:minor root mountpoint options [optional...] - fstype source superoptions
            var parts = line.Split(' ');
            var sep = Array.IndexOf(parts, "-");
            if (parts.Length < 7 || sep < 6 || sep + 1 >= parts.Length) continue;
            var mountPoint = Unescape(parts[4]);
            if (!PathValidator.IsSameOrInside(path, mountPoint) && mountPoint != "/") continue;
            if (best is not null && best.MountPoint.Length > mountPoint.Length) continue;
            var options = parts[5].Split(',');
            best = new MountInfo(mountPoint, parts[sep + 1], options.Contains("ro"));
        }

        return best;
    }

    private static string Unescape(string s) => s.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}

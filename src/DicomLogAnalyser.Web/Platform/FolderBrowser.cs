using DicomLogAnalyser.Core.Sources;

namespace DicomLogAnalyser.Web.Platform;

public sealed record FolderEntry(string Name, string Path);

public sealed record FolderListing(string? Path, string? Parent, IReadOnlyList<FolderEntry> Folders, int Files, bool Truncated);

/// <summary>
/// In-app folder browser: lists sub-folders so a folder can be chosen without typing its path. In central mode only
/// the read-only source roots and folders inside them can be listed; links are not followed.
/// </summary>
public sealed class FolderBrowser(AppOptions options)
{
    public const int MaxEntries = 2000;

    private bool Confined => options.Mode == AppMode.Server;

    public FolderListing List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new FolderListing(null, null, Roots(), 0, false);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Enter a full path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length == 0) full = Path.DirectorySeparatorChar.ToString();
        if (Confined && !options.SourceRoots.Any(r => PathValidator.IsSameOrInside(full, Path.GetFullPath(r))))
            throw new UnauthorizedAccessException("Only the mounted source folders can be browsed.");
        var dir = new DirectoryInfo(full);
        if (!dir.Exists) throw new DirectoryNotFoundException();
        if (Confined && dir.LinkTarget is not null) throw new UnauthorizedAccessException("Links are not followed.");

        var folders = new List<FolderEntry>();
        var files = 0;
        var truncated = false;
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
        {
            if (entry is FileInfo)
            {
                files++;
                continue;
            }

            if (entry.LinkTarget is not null && Confined) continue;
            if (folders.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            folders.Add(new FolderEntry(entry.Name, entry.FullName));
        }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        string? parent = dir.Parent?.FullName;
        if (Confined && parent is not null && !options.SourceRoots.Any(r => PathValidator.IsSameOrInside(parent, Path.GetFullPath(r)))) parent = null;
        return new FolderListing(dir.FullName, parent, folders, files, truncated);
    }

    private List<FolderEntry> Roots()
    {
        if (Confined) return options.SourceRoots.Select(r => new FolderEntry(r, Path.GetFullPath(r))).ToList();
        var roots = new List<FolderEntry>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                roots.Add(new FolderEntry(d.Name + (d.DriveType == DriveType.Network ? "  (network drive)" : ""), d.RootDirectory.FullName));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS()) roots.Add(new FolderEntry("Volumes (disks and network shares)", "/Volumes"));
            roots.Add(new FolderEntry("Home folder", home));
            roots.Add(new FolderEntry("Computer (/)", "/"));
        }

        return roots;
    }
}

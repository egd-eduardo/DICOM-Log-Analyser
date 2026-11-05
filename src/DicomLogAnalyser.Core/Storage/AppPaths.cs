namespace DicomLogAnalyser.Core.Storage;

/// <summary>Application data locations. Everything the application writes lives under <see cref="Root"/>.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "index", "index.db");
    public string BundlesDirectory => Path.Combine(Root, "bundles");
    public string TempDirectory => Path.Combine(Root, "tmp");

    /// <summary>Per-user default: %LOCALAPPDATA%, ~/Library/Application Support or $XDG_DATA_HOME.</summary>
    public static string DefaultLocalRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DicomLogAnalyser");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "DicomLogAnalyser");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "DicomLogAnalyser");
    }

    /// <summary>Creates the directories with owner-only permissions on macOS/Linux (Windows inherits the profile ACL).</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Path.GetDirectoryName(DatabaseFile)!, BundlesDirectory, TempDirectory })
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>Deletes leftover temporary upload and extraction files. Returns the number of entries that remain.</summary>
    public int CleanTemp()
    {
        if (!Directory.Exists(TempDirectory)) return 0;
        var remaining = 0;
        foreach (var entry in new DirectoryInfo(TempDirectory).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo d) d.Delete(recursive: true);
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
            catch (IOException)
            {
                remaining++;
            }
            catch (UnauthorizedAccessException)
            {
                remaining++;
            }
        }

        return remaining;
    }
}

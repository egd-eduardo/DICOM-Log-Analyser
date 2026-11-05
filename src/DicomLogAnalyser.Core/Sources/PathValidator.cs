using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Parsing;
using DicomLogAnalyser.Core.Storage;

namespace DicomLogAnalyser.Core.Sources;

public sealed record ValidationResult(string? NormalisedPath, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Info)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Validates source settings and checks a folder path from the point of view of this process.</summary>
public sealed partial class PathValidator
{
    private readonly AppPaths _paths;
    private readonly List<string> _allowedRoots;

    /// <param name="allowedRoots">When non-empty (central mode), folder sources must lie inside one of these directories.</param>
    public PathValidator(AppPaths paths, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _paths = paths;
        _allowedRoots = allowedRoots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    [GeneratedRegex(@"^[A-Za-z0-9 ._*?\-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex PatternRegex();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetterRegex();

    public static string? CheckLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Enter a label for the source.";
        if (label.Length > 100) return "The label must be at most 100 characters.";
        if (label.Any(char.IsControl)) return "The label contains control characters.";
        return null;
    }

    public static string? CheckPatterns(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return "Enter at least one file name pattern, for example * or *.log.";
        var parts = SplitPatterns(patterns);
        if (parts.Count == 0 || parts.Count > 20 || parts.Any(p => !PatternRegex().IsMatch(p)))
            return "File name patterns may contain letters, digits, spaces, . _ - * ? and are separated by ';' (at most 20).";
        return null;
    }

    public static IReadOnlyList<string> SplitPatterns(string patterns) =>
        (patterns ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? CheckTimeZone(string? tz)
    {
        if (string.IsNullOrEmpty(tz)) return null;
        if (tz.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) return "Unknown time zone. Use an IANA name such as Europe/Lisbon or a Windows name such as GMT Standard Time.";
        return null;
    }

    public static string? CheckFormat(string? format) =>
        FormatRegistry.IsValidHint(format) ? null : "Unknown log format.";

    /// <summary>Checks a folder path: syntax, confinement, existence, read access, mount and share hints.</summary>
    public ValidationResult ValidateFolder(string? rawPath, int sampleLimit = 2000)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var info = new List<string>();
        var path = rawPath?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            errors.Add("Enter a folder path.");
            return new ValidationResult(null, errors, warnings, info);
        }

        if (path.Length > 1024 || path.Any(char.IsControl))
        {
            errors.Add("The path is too long or contains control characters.");
            return new ValidationResult(null, errors, warnings, info);
        }

        var looksWindows = DriveLetterRegex().IsMatch(path) || path.StartsWith(@"\\", StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows() && looksWindows)
        {
            errors.Add("This is a Windows path, but the analyser is running on " + (OperatingSystem.IsMacOS() ? "macOS" : "Linux (for example inside Docker)") +
                       ". A container cannot open Windows drive letters or UNC shares directly: mount the share on the Docker host, add it to compose.yaml as a read-only volume, and enter the container path (for example /sources/gateway).");
            return new ValidationResult(null, errors, warnings, info);
        }

        if (!Path.IsPathFullyQualified(path))
        {
            errors.Add("Enter a full path, for example " + (OperatingSystem.IsWindows() ? @"C:\3dnet\DicomRouter\logs or \\server\share\logs" : "/Volumes/ClinicalLogs/Gateway or /sources/gateway") + ".");
            return new ValidationResult(null, errors, warnings, info);
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add("The path is not valid on this system.");
            return new ValidationResult(null, errors, warnings, info);
        }

        if (_allowedRoots.Count > 0)
        {
            var root = _allowedRoots.FirstOrDefault(r => IsSameOrInside(full, r));
            if (root is null)
            {
                errors.Add("In central mode, sources must be inside a read-only mounted folder: " + string.Join(", ", _allowedRoots) + ". Add a volume in compose.yaml and use its container path.");
                return new ValidationResult(full, errors, warnings, info);
            }

            if (HasLinkBelow(root, full))
            {
                errors.Add("The path goes through a symbolic link. Use the real folder inside the mounted source root.");
                return new ValidationResult(full, errors, warnings, info);
            }
        }

        if (IsSameOrInside(full, _paths.Root) || IsSameOrInside(_paths.Root, full))
        {
            errors.Add("The source folder overlaps the analyser's own data folder. Choose the folder that contains the logs.");
            return new ValidationResult(full, errors, warnings, info);
        }

        if (OperatingSystem.IsWindows() && DriveLetterRegex().IsMatch(full))
        {
            try
            {
                if (new DriveInfo(full[..1]).DriveType == DriveType.Network)
                    warnings.Add("This is a mapped network drive. Mapped drive letters exist only in the signed-in user's session; if the analyser runs as a service or another user, use the UNC path (\\\\server\\share\\...) instead.");
            }
            catch (ArgumentException)
            {
            }
        }

        if (!Directory.Exists(full))
        {
            errors.Add(File.Exists(full)
                ? "The path is a file. Enter the folder that contains the log files."
                : "The folder does not exist or is not reachable from this computer (share offline, VPN disconnected, or no permission).");
            return new ValidationResult(full, errors, warnings, info);
        }

        try
        {
            var enumerator = new FileEnumerator(full, recursive: true, ["*"]);
            var count = 0;
            foreach (var _ in enumerator.Enumerate())
            {
                if (++count >= sampleLimit) break;
            }

            info.Add(count >= sampleLimit ? $"At least {sampleLimit} files found." : $"{count} file(s) found.");
            if (count == 0) warnings.Add("The folder is readable but contains no files.");
            if (enumerator.InaccessibleDirectories > 0) warnings.Add($"{enumerator.InaccessibleDirectories} subfolder(s) could not be read (permissions).");
            if (enumerator.SkippedLinks > 0) info.Add($"{enumerator.SkippedLinks} symbolic link(s) will be ignored.");
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add("Access denied. The account running the analyser needs read permission on this folder.");
            return new ValidationResult(full, errors, warnings, info);
        }
        catch (IOException)
        {
            errors.Add("The folder could not be read (I/O error or share unavailable).");
            return new ValidationResult(full, errors, warnings, info);
        }

        var mount = MountInfo.Find(full);
        if (mount is not null)
        {
            if (!mount.ReadOnly) warnings.Add("This location is mounted read-write. The analyser only opens files for reading, but a read-only mount (\":ro\" in compose.yaml) is a recommended safeguard.");
            else info.Add("Mounted read-only.");
            if (StorageGuard.IsNetworkFileSystem(mount.FileSystem)) info.Add("Network file system: scans depend on the share being reachable.");
        }

        return new ValidationResult(full, errors, warnings, info);
    }

    public static bool IsSameOrInside(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var p = Path.TrimEndingDirectorySeparator(path);
        var r = Path.TrimEndingDirectorySeparator(root);
        if (p.Equals(r, comparison)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, comparison) || (r.EndsWith(Path.DirectorySeparatorChar) && p.StartsWith(r, comparison));
    }

    private static bool HasLinkBelow(string root, string full)
    {
        var current = new DirectoryInfo(full);
        while (current is not null && !string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, StringComparison.Ordinal))
        {
            if (current.Exists && current.LinkTarget is not null) return true;
            current = current.Parent;
        }

        return false;
    }
}

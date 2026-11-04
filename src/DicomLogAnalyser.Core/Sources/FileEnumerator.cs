namespace DicomLogAnalyser.Core.Sources;

/// <summary>
/// Enumerates candidate log files without following symbolic links or junctions (prevents escaping a mounted root and
/// enumeration loops). Inaccessible subfolders are counted, not fatal.
/// </summary>
public sealed class FileEnumerator(string root, bool recursive, IReadOnlyList<string> patterns)
{
    public const int MaxDepth = 32;
    public const int MaxFiles = 200_000;

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz", ".zst", ".tar", ".exe", ".dll", ".pdb", ".dcm", ".db", ".sqlite",
        ".db-wal", ".db-shm", ".png", ".jpg", ".jpeg", ".pdf", ".evtx", ".etl",
    };

    public int InaccessibleDirectories { get; private set; }
    public int SkippedLinks { get; private set; }
    public int SkippedUnsupported { get; private set; }
    public bool Truncated { get; private set; }

    public IEnumerable<FileInfo> Enumerate()
    {
        var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
        stack.Push((new DirectoryInfo(root), 0));
        var yielded = 0;
        var first = true;
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0,
                }).ToList();
            }
            catch (Exception ex) when (!first && ex is UnauthorizedAccessException or IOException)
            {
                InaccessibleDirectories++;
                continue;
            }

            first = false;
            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    SkippedLinks++;
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    if (recursive && depth + 1 < MaxDepth) stack.Push((sub, depth + 1));
                    continue;
                }

                if (entry is not FileInfo file || !Matches(file.Name)) continue;
                if (SkippedExtensions.Contains(Path.GetExtension(file.Name)))
                {
                    SkippedUnsupported++;
                    continue;
                }

                if (++yielded > MaxFiles)
                {
                    Truncated = true;
                    yield break;
                }

                yield return file;
            }
        }
    }

    private bool Matches(string name) => patterns.Any(p => Glob(p, name));

    /// <summary>Case-insensitive wildcard match supporting * and ?.</summary>
    internal static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

namespace DicomLogAnalyser.Core.Sources;

public static class SourceKinds
{
    public const string Folder = "folder";
    public const string Bundle = "bundle";
}

/// <summary>A configured log location: a folder reachable from this computer/container, or an imported bundle.</summary>
public sealed record SourceDefinition
{
    public long Id { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Recursive { get; init; } = true;
    public string IncludePatterns { get; init; } = "*";

    /// <summary>IANA or Windows time-zone id used for logs whose timestamps have no UTC offset; null when unknown.</summary>
    public string? TimeZone { get; init; }

    public string FormatHint { get; init; } = "auto";
    public string? CreatedUtc { get; init; }
    public string? LastScanUtc { get; init; }
    public string? LastScanStatus { get; init; }
    public string? LastScanMessage { get; init; }
    public string? BundleSha256 { get; init; }
    public long? BundleBytes { get; init; }

    /// <summary>Folder sources: relative paths of the files chosen for analysis; null until a selection exists.</summary>
    public IReadOnlyList<string>? SelectedFiles { get; init; }

    /// <summary>When the selection was last saved (files modified later are reported as new).</summary>
    public string? SelectionUtc { get; init; }

    /// <summary>All files present in the folder when the selection was made; files not in this list are "new".</summary>
    public IReadOnlyList<string>? SeenFiles { get; init; }

    /// <summary>Files in the folder that were not analysed at the last scan, and how many of them are new.</summary>
    public int UnselectedFiles { get; init; }

    public int NewFiles { get; init; }
}

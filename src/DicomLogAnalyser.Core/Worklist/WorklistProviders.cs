using DicomLogAnalyser.Core.Parsing;

namespace DicomLogAnalyser.Core.Worklist;

/// <summary>A modality worklist provider whose logs the analyser understands.</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Name">Name shown as the provider (a value, not a heading).</param>
/// <param name="FormatId">The log format of this provider's worklist service.</param>
/// <param name="FolderHint">Where its logs usually are, relative to the provider's installation folder.</param>
public sealed record WorklistProvider(string Id, string Name, string FormatId, string FolderHint);

/// <summary>
/// The supported modality worklist providers. The interface stays provider-neutral and shows the provider only as a
/// value; adding a provider means adding its log format and one entry here.
/// </summary>
public static class WorklistProviders
{
    public static IReadOnlyList<WorklistProvider> All { get; } =
    [
        new("vidacare", "VIDAcare", VidacareConnectorFormat.FormatId, @"Connector\logs"),
    ];

    public static WorklistProvider? ForFormat(string? formatId) =>
        All.FirstOrDefault(p => string.Equals(p.FormatId, formatId, StringComparison.Ordinal));
}

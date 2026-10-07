using RevitAi.Core.Tools;

namespace RevitAi.Core.Context;

/// <summary>
/// What the user is currently looking at in Revit. Shown in the panel's context indicator and sent to the AI
/// with every request, including a short preview of the selection so follow-ups like "make it longer" need no lookup.
/// </summary>
public sealed record ModelContext(
    string? DocumentTitle,
    string? ViewName,
    string? ViewType,
    string? LevelName,
    int SelectionCount,
    IReadOnlyList<ElementSummary>? SelectionPreview = null,
    long? ViewId = null)
{
    /// <summary>Selected elements included in <see cref="SelectionPreview"/>; the rest are counted only.</summary>
    public const int MaxSelectionPreview = 5;

    public static ModelContext NoDocument { get; } = new(null, null, null, null, 0);

    public bool HasDocument => DocumentTitle is not null;
}

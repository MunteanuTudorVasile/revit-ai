namespace RevitAi.Core.Context;

/// <summary>What the user is currently looking at in Revit. Shown in the panel's context indicator.</summary>
public sealed record ModelContext(
    string? DocumentTitle,
    string? ViewName,
    string? ViewType,
    string? LevelName,
    int SelectionCount)
{
    public static ModelContext NoDocument { get; } = new(null, null, null, null, 0);

    public bool HasDocument => DocumentTitle is not null;
}

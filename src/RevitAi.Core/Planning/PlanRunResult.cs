namespace RevitAi.Core.Planning;

/// <param name="ElementId">The element the step created or changed, when it has a single main element.</param>
public sealed record StepResult(int Number, string ToolName, bool Succeeded, string Outcome, IReadOnlyList<string> Warnings, long? ElementId = null);

/// <summary>
/// Outcome of previewing or applying a plan. When <see cref="Succeeded"/> is false, nothing was changed:
/// the steps up to and including the failed one are reported, and the whole plan was rolled back.
/// </summary>
public sealed record PlanRunResult(
    bool Succeeded,
    bool Applied,
    IReadOnlyList<StepResult> Steps,
    IReadOnlyList<long> AffectedElementIds,
    string? UndoName)
{
    public StepResult? FailedStep => Steps.FirstOrDefault(s => !s.Succeeded);
}

using System.Text.Json;
using Autodesk.Revit.DB;
using RevitAi.Addin.Tools;
using RevitAi.Core.Localization;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Planning;

/// <summary>
/// Runs a plan inside Revit (ADR-024, ADR-027). Must be called inside Revit's API context (through the dispatcher).
/// Every operation runs in its own transaction inside one transaction group:
/// preview always rolls the group back; apply assimilates it into a single undo entry,
/// unless any step fails, in which case everything is rolled back.
/// </summary>
public sealed class PlanExecutor
{
    private const int MaxUndoNameLength = 60;

    private readonly ToolRegistry _registry;
    private readonly TextSource _text;

    public PlanExecutor(ToolRegistry registry, TextSource text)
    {
        _registry = registry;
        _text = text;
    }

    public PlanRunResult Run(Document document, PendingPlan plan, bool apply)
    {
        string undoName = UndoName(plan.UserRequest);
        var createdIds = new Dictionary<int, long>();
        var affectedIds = new List<long>();
        var steps = new List<StepResult>();

        using var group = new TransactionGroup(document, undoName);
        group.Start();

        foreach (PlannedOperation operation in plan.Operations)
        {
            StepResult step = RunStep(document, operation, createdIds, affectedIds);
            steps.Add(step);
            if (!step.Succeeded)
            {
                break;
            }
        }

        bool succeeded = steps.All(s => s.Succeeded);
        bool applied = succeeded && apply;
        if (applied)
        {
            group.Assimilate();
        }
        else
        {
            group.RollBack();
        }

        return new PlanRunResult(succeeded, applied, steps, applied ? affectedIds.Distinct().ToList() : [], applied ? undoName : null);
    }

    private StepResult RunStep(Document document, PlannedOperation operation, Dictionary<int, long> createdIds, List<long> affectedIds)
    {
        if (!_registry.TryGet(operation.ToolName, out ITool registered) || registered is not RevitWriteTool tool)
        {
            return Failed(operation, _text.Current.Format("Plan.NotWriteTool", operation.ToolName), []);
        }

        var failures = new FailureCollector();
        using var transaction = new Transaction(document, $"{operation.Number}. {operation.ToolName}");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures)
            .SetClearAfterRollback(true));
        transaction.Start();

        OperationResult result;
        try
        {
            using JsonDocument arguments = JsonDocument.Parse(operation.ArgumentsJson);
            result = tool.Apply(document, PlanReferences.Resolve(arguments.RootElement, createdIds));
        }
        catch (Exception ex)
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
            {
                transaction.RollBack();
            }

            string reason = ex is ToolException ? ex.Message : _text.Current.Format("Plan.RevitRefused", ex.Message);
            return Failed(operation, reason, failures.Warnings);
        }

        TransactionStatus status = transaction.Commit();
        if (status != TransactionStatus.Committed)
        {
            string reason = failures.Errors.Count > 0 ? string.Join(" ", failures.Errors) : _text.Current.Format("Plan.NotAccepted", status);
            return Failed(operation, reason, failures.Warnings);
        }

        createdIds[operation.Number] = result.ElementId;
        affectedIds.Add(result.ElementId);
        return new StepResult(operation.Number, operation.ToolName, true, result.Outcome, failures.Warnings);
    }

    private static StepResult Failed(PlannedOperation operation, string reason, IReadOnlyList<string> warnings) =>
        new(operation.Number, operation.ToolName, false, reason, warnings);

    private static string UndoName(string userRequest)
    {
        string request = userRequest.ReplaceLineEndings(" ").Trim();
        return "Revit AI: " + (request.Length <= MaxUndoNameLength ? request : request[..MaxUndoNameLength] + "…");
    }
}

/// <summary>
/// Collects Revit warnings and errors instead of showing dialogs mid-action (ADR-027).
/// Warnings are dismissed and reported; any error rolls the transaction back.
/// </summary>
internal sealed class FailureCollector : IFailuresPreprocessor
{
    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        bool hasErrors = false;
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            string text = failure.GetDescriptionText();
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                Warnings.Add(text);
                failuresAccessor.DeleteWarning(failure);
            }
            else
            {
                Errors.Add(text);
                hasErrors = true;
            }
        }

        return hasErrors ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}

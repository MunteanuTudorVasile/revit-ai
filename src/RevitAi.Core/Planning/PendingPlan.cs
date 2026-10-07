using RevitAi.Core.Tools;

namespace RevitAi.Core.Planning;

public sealed record PlannedOperation(int Number, string ToolName, string ArgumentsJson, string Summary, RiskLevel Risk);

/// <summary>
/// Model changes proposed by the AI for one user request, not yet applied (ADR-024).
/// Risk and whether a preview is required are computed here, never by the AI (ADR-025).
/// </summary>
public sealed class PendingPlan
{
    /// <summary>A plan with more operations than this counts as a large modification.</summary>
    public const int LargePlanThreshold = 5;

    private readonly List<PlannedOperation> _operations = [];

    public PendingPlan(string userRequest)
    {
        UserRequest = userRequest;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string UserRequest { get; }

    public IReadOnlyList<PlannedOperation> Operations => _operations;

    public int NextNumber => _operations.Count + 1;

    public RiskLevel Risk
    {
        get
        {
            if (_operations.Count == 0)
            {
                return RiskLevel.ReadOnly;
            }

            RiskLevel highest = _operations.Max(o => o.Risk);
            RiskLevel bySize = _operations.Count > LargePlanThreshold ? RiskLevel.LargeModification : RiskLevel.SafeModification;
            return highest > bySize ? highest : bySize;
        }
    }

    /// <summary>Large plans must be previewed before Apply is enabled (UX risk level 3).</summary>
    public bool RequiresPreview => Risk >= RiskLevel.LargeModification;

    public PlannedOperation Add(string toolName, string argumentsJson, string summary, RiskLevel risk)
    {
        var operation = new PlannedOperation(NextNumber, toolName, argumentsJson, summary, risk);
        _operations.Add(operation);
        return operation;
    }
}

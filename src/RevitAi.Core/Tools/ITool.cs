using System.Text.Json;

namespace RevitAi.Core.Tools;

public enum RiskLevel
{
    ReadOnly,
    SafeModification,
    LargeModification,
    Destructive,
}

/// <summary>An operation the AI may request. The only way the AI reaches Revit (ADR-005).</summary>
public interface ITool
{
    /// <summary>Stable snake_case name; part of the AI contract.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the arguments, limited to what OpenAI strict mode accepts (see <see cref="SchemaValidator"/>).</summary>
    JsonElement InputSchema { get; }

    /// <summary>Fixed per tool; never taken from the AI (ADR-025).</summary>
    RiskLevel Risk { get; }

    /// <summary>Shown in the panel while the tool runs, e.g. "Reading selection…".</summary>
    string ProgressLabel { get; }
}

/// <summary>Reads the model and returns data to the AI immediately.</summary>
public interface IReadTool : ITool
{
    /// <summary>Returns a result object that is serialized to JSON for the AI. Throws <see cref="ToolException"/> for expected failures.</summary>
    Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Proposes a model change. Calling it never changes the model: the operation is checked and
/// queued into a plan that the user previews and applies (ADR-024).
/// </summary>
public interface IWriteTool : ITool
{
    /// <summary>
    /// Checks the operation against the current model without changing it and returns a one-line description
    /// for the plan. IDs given as plan references (<c>$opN.elementId</c>) cannot be checked yet; they are checked
    /// when the plan runs. Throws <see cref="ToolException"/> when the operation is invalid.
    /// </summary>
    Task<string> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>An expected tool failure whose message is safe and useful to show the AI and the user.</summary>
public sealed class ToolException(string message) : Exception(message);

public static class ToolSchema
{
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

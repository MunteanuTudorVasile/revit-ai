using System.Text.Encodings.Web;
using System.Text.Json;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

public sealed record ToolCallRecord(string Name, string ArgumentsJson, bool Succeeded);

/// <param name="Plan">Changes the AI proposed in this turn, not yet applied; null when it proposed none.</param>
public sealed record AssistantReply(
    string Text,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    bool StoppedAtStepLimit,
    PendingPlan? Plan);

/// <summary>
/// The AI loop: ask the model, run the tools it requests, feed the results back, repeat until it answers.
/// Read tools run immediately. Write tools are only validated and queued into a plan (ADR-024).
/// It never touches Revit directly; tools do, through the dispatcher.
/// </summary>
public sealed class Orchestrator
{
    public const string ThinkingLabel = "Thinking…";

    // Tool output goes to the AI, not into HTML: keep names like "Instalații" readable instead of \u escapes.
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAiClient _ai;
    private readonly ToolRegistry _registry;
    private readonly FileLog _log;
    private readonly int _maxSteps;
    private readonly int _maxResultChars;

    public Orchestrator(IAiClient ai, ToolRegistry registry, FileLog log, int maxSteps = 8, int maxResultChars = 20_000)
    {
        _ai = ai;
        _registry = registry;
        _log = log;
        _maxSteps = maxSteps;
        _maxResultChars = maxResultChars;
    }

    /// <summary>
    /// Runs one user turn. The turn is added to <paramref name="conversation"/> only when it completes,
    /// so a failure or cancellation leaves the history unchanged.
    /// </summary>
    public async Task<AssistantReply> RunAsync(
        Conversation conversation,
        string userText,
        ModelContext context,
        IReadOnlyList<ActionRecord> recentActions,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var turn = new List<AiItem> { new UserMessage(userText) };
        var records = new List<ToolCallRecord>();
        var plan = new PendingPlan(userText);
        string instructions = AssistantInstructions.Build(context, recentActions);

        for (int step = 0; step < _maxSteps; step++)
        {
            progress?.Report(ThinkingLabel);
            AiResponse response = await _ai.CompleteAsync(
                new AiRequest(instructions, [.. conversation.Items, .. turn], _registry.Tools),
                cancellationToken).ConfigureAwait(false);

            if (response.ToolCalls.Count == 0)
            {
                string text = string.IsNullOrWhiteSpace(response.Text) ? "I don't have an answer for that." : response.Text;
                turn.Add(new AssistantMessage(text));
                conversation.AddTurn(turn);
                return new AssistantReply(text, records, StoppedAtStepLimit: false, PlanOrNull(plan));
            }

            var outputs = new List<AiItem>();
            foreach (ToolCall call in response.ToolCalls)
            {
                (string content, bool succeeded) = await RunToolAsync(call, plan, progress, cancellationToken).ConfigureAwait(false);
                outputs.Add(new ToolOutput(call.Id, content));
                records.Add(new ToolCallRecord(call.Name, call.ArgumentsJson, succeeded));
            }

            turn.AddRange(response.ToolCalls);
            turn.AddRange(outputs);
        }

        string stopped = $"I stopped after {_maxSteps} steps without reaching an answer. Try asking something more specific.";
        turn.Add(new AssistantMessage(stopped));
        conversation.AddTurn(turn);
        return new AssistantReply(stopped, records, StoppedAtStepLimit: true, PlanOrNull(plan));
    }

    private static PendingPlan? PlanOrNull(PendingPlan plan) => plan.Operations.Count == 0 ? null : plan;

    private async Task<(string Content, bool Succeeded)> RunToolAsync(
        ToolCall call,
        PendingPlan plan,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(call.Name, out ITool tool))
        {
            return (Error($"Unknown tool '{call.Name}'. Use only the tools provided."), false);
        }

        JsonElement arguments;
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (Error("The arguments are not valid JSON."), false);
        }

        IReadOnlyList<string> errors = SchemaValidator.Validate(tool.InputSchema, arguments);
        if (errors.Count > 0)
        {
            return (Error("Invalid arguments: " + string.Join(" ", errors)), false);
        }

        if (tool is IWriteTool)
        {
            int badReference = PlanReferences.Find(arguments).FirstOrDefault(n => n < 1 || n >= plan.NextNumber);
            if (badReference != 0)
            {
                return (Error($"{PlanReferences.For(badReference)} does not refer to an earlier operation in this plan."), false);
            }
        }

        progress?.Report(tool.ProgressLabel);
        try
        {
            return tool switch
            {
                IReadTool read => (Serialize(await read.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)), true),
                IWriteTool write => (await QueueAsync(write, call, arguments, plan, cancellationToken).ConfigureAwait(false), true),
                _ => (Error($"Tool '{call.Name}' cannot be run."), false),
            };
        }
        catch (ToolException ex)
        {
            return (Error(ex.Message), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return (Error("Revit did not respond in time. It may be busy or showing a dialog."), false);
        }
        catch (Exception ex)
        {
            _log.Error($"Tool {call.Name} failed with arguments {call.ArgumentsJson}.", ex);
            return (Error($"The tool failed unexpectedly: {ex.Message}"), false);
        }
    }

    private async Task<string> QueueAsync(
        IWriteTool tool,
        ToolCall call,
        JsonElement arguments,
        PendingPlan plan,
        CancellationToken cancellationToken)
    {
        string summary = await tool.ValidateAsync(arguments, cancellationToken).ConfigureAwait(false);
        PlannedOperation operation = plan.Add(tool.Name, arguments.GetRawText(), summary, tool.Risk);
        return Serialize(new
        {
            queued = true,
            operation = operation.Number,
            reference = PlanReferences.For(operation.Number),
            summary,
            note = "Not applied. The user must review the plan and click Apply.",
        });
    }

    private string Serialize(object result)
    {
        string json = JsonSerializer.Serialize(result, result.GetType(), ResultJson);
        return json.Length <= _maxResultChars
            ? json
            : $"[Result truncated to {_maxResultChars} of {json.Length} characters. Ask for fewer items.]\n{json[.._maxResultChars]}";
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, ResultJson);
}

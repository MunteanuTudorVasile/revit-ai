using System.Text.Encodings.Web;
using System.Text.Json;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

public sealed record ToolCallRecord(string Name, string ArgumentsJson, bool Succeeded);

public sealed record AssistantReply(string Text, IReadOnlyList<ToolCallRecord> ToolCalls, bool StoppedAtStepLimit);

/// <summary>
/// The AI loop: ask the model, run the tools it requests, feed the results back, repeat until it answers.
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
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var turn = new List<AiItem> { new UserMessage(userText) };
        var records = new List<ToolCallRecord>();
        string instructions = AssistantInstructions.Build(context);

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
                return new AssistantReply(text, records, StoppedAtStepLimit: false);
            }

            var outputs = new List<AiItem>();
            foreach (ToolCall call in response.ToolCalls)
            {
                (string content, bool succeeded) = await RunToolAsync(call, progress, cancellationToken).ConfigureAwait(false);
                outputs.Add(new ToolOutput(call.Id, content));
                records.Add(new ToolCallRecord(call.Name, call.ArgumentsJson, succeeded));
            }

            turn.AddRange(response.ToolCalls);
            turn.AddRange(outputs);
        }

        string stopped = $"I stopped after {_maxSteps} steps without reaching an answer. Try asking something more specific.";
        turn.Add(new AssistantMessage(stopped));
        conversation.AddTurn(turn);
        return new AssistantReply(stopped, records, StoppedAtStepLimit: true);
    }

    private async Task<(string Content, bool Succeeded)> RunToolAsync(
        ToolCall call,
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

        progress?.Report(tool.ProgressLabel);
        try
        {
            object result = await tool.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
            return (Truncate(JsonSerializer.Serialize(result, result.GetType(), ResultJson)), true);
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

    private string Truncate(string json) =>
        json.Length <= _maxResultChars
            ? json
            : $"[Result truncated to {_maxResultChars} of {json.Length} characters. Ask for fewer items.]\n{json[.._maxResultChars]}";

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, ResultJson);
}

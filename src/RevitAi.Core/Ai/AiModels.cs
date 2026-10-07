using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

/// <summary>One entry in the conversation sent to the AI provider.</summary>
public abstract record AiItem;

public sealed record UserMessage(string Text) : AiItem;

public sealed record AssistantMessage(string Text) : AiItem;

/// <param name="ExtraContentJson">
/// Provider data attached to the call (e.g. Gemini's thought signature in <c>extra_content</c>), kept verbatim and sent back
/// unchanged with the conversation (ADR-046).
/// </param>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson, string? ExtraContentJson = null) : AiItem;

public sealed record ToolOutput(string CallId, string Content) : AiItem;

public sealed record AiRequest(string Instructions, IReadOnlyList<AiItem> Items, IReadOnlyCollection<ITool> Tools);

/// <summary>Either a final answer (<see cref="Text"/>) or tool calls to run before asking again.</summary>
public sealed record AiResponse(string? Text, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>Provider-neutral AI client (ADR-003). Provider details must not leak past this interface.</summary>
public interface IAiClient
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

public enum AiFailure
{
    MissingApiKey,
    InvalidApiKey,
    RateLimited,
    ServiceError,
    BadResponse,

    /// <summary>The service is temporarily overloaded (HTTP 502/503/504), even after retrying.</summary>
    Busy,
}

public sealed class AiServiceException(AiFailure failure, string message) : Exception(message)
{
    public AiFailure Failure { get; } = failure;
}

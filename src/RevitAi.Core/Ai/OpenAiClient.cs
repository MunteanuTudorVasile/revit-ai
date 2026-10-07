using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

/// <summary>
/// OpenAI Chat Completions with strict function calling, over plain HttpClient
/// (no SDK, to avoid assembly conflicts inside Revit; ADR-032).
/// </summary>
public sealed class OpenAiClient : IAiClient
{
    public static readonly Uri Endpoint = new("https://api.openai.com/v1/chat/completions");

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Func<string?> _apiKeyProvider;

    /// <param name="apiKeyProvider">Read on every request so a newly saved key takes effect immediately.</param>
    public OpenAiClient(HttpClient http, string model, Func<string?> apiKeyProvider)
    {
        _http = http;
        _model = model;
        _apiKeyProvider = apiKeyProvider;
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiServiceException(AiFailure.MissingApiKey, "No OpenAI API key is set.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body);
        }

        return ParseResponse(body);
    }

    internal JsonObject BuildBody(AiRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.Instructions },
        };

        JsonArray? pendingToolCalls = null;
        foreach (AiItem item in request.Items)
        {
            if (item is ToolCall call)
            {
                // Consecutive tool calls belong to one assistant message.
                if (pendingToolCalls is null)
                {
                    pendingToolCalls = [];
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = pendingToolCalls });
                }

                pendingToolCalls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                });
                continue;
            }

            pendingToolCalls = null;
            messages.Add(item switch
            {
                UserMessage user => new JsonObject { ["role"] = "user", ["content"] = user.Text },
                AssistantMessage assistant => new JsonObject { ["role"] = "assistant", ["content"] = assistant.Text },
                ToolOutput output => new JsonObject { ["role"] = "tool", ["tool_call_id"] = output.CallId, ["content"] = output.Content },
                _ => throw new InvalidOperationException($"Unsupported conversation item {item.GetType().Name}."),
            });
        }

        var body = new JsonObject { ["model"] = _model, ["messages"] = messages };

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                    ["strict"] = true,
                },
            }).ToArray());
        }

        return body;
    }

    internal static AiResponse ParseResponse(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement message = document.RootElement.GetProperty("choices")[0].GetProperty("message");

            var toolCalls = new List<ToolCall>();
            if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    JsonElement function = call.GetProperty("function");
                    toolCalls.Add(new ToolCall(
                        call.GetProperty("id").GetString()!,
                        function.GetProperty("name").GetString()!,
                        function.GetProperty("arguments").GetString() ?? "{}"));
                }
            }

            string? text = StringOrNull(message, "content") ?? StringOrNull(message, "refusal");
            return new AiResponse(text, toolCalls);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new AiServiceException(AiFailure.BadResponse, "OpenAI returned a response that could not be read.");
        }
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static AiServiceException MapError(HttpStatusCode status, string body)
    {
        string detail = ExtractErrorMessage(body) ?? $"HTTP {(int)status}";
        return status switch
        {
            HttpStatusCode.Unauthorized => new AiServiceException(AiFailure.InvalidApiKey, detail),
            HttpStatusCode.TooManyRequests => new AiServiceException(AiFailure.RateLimited, detail),
            _ => new AiServiceException(AiFailure.ServiceError, $"HTTP {(int)status}: {detail}"),
        };
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                   && error.TryGetProperty("message", out JsonElement message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

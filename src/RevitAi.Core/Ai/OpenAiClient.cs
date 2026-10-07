using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

/// <summary>
/// Chat Completions with strict function calling in the OpenAI format, over plain HttpClient (no SDK, to avoid assembly
/// conflicts inside Revit; ADR-032). Works with any provider that accepts this format, e.g. OpenAI and Google Gemini (ADR-046).
/// </summary>
public sealed class OpenAiClient : IAiClient
{
    /// <summary>Waits before retrying a temporarily overloaded service (HTTP 502/503/504).</summary>
    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly HttpClient _http;
    private readonly Func<AiConnection> _connection;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    /// <param name="connection">Read on every request, so a newly saved key, service or model takes effect immediately.</param>
    /// <param name="retryDelays">Delays between retries when the service is busy; tests pass zeros.</param>
    public OpenAiClient(HttpClient http, Func<AiConnection> connection, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _http = http;
        _connection = connection;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        AiConnection connection = _connection();
        if (string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            throw new AiServiceException(AiFailure.MissingApiKey, $"No API key is set for {connection.Provider.DisplayName}.");
        }

        string json = BuildBody(request, connection.Model, connection.Provider).ToJsonString();
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, connection.Provider.Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);

            using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ParseResponse(body);
            }

            bool busy = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (busy && attempt < _retryDelays.Count)
            {
                await Task.Delay(_retryDelays[attempt], cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw MapError(response.StatusCode, body);
        }
    }

    /// <summary>
    /// Gemini 3 rejects a tool call sent back without its thought signature. For calls that have none (e.g. history from
    /// another model), Google documents this placeholder, base64-encoded as the signature is binary.
    /// </summary>
    internal static readonly string SkipThoughtSignature =
        Convert.ToBase64String(Encoding.UTF8.GetBytes("skip_thought_signature_validator"));

    internal static JsonObject BuildBody(AiRequest request, string model, AiProvider? provider = null)
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

                var toolCall = new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                };
                if (call.ExtraContentJson is not null)
                {
                    toolCall["extra_content"] = JsonNode.Parse(call.ExtraContentJson);
                }
                else if (provider == AiProviders.Gemini)
                {
                    toolCall["extra_content"] = new JsonObject
                    {
                        ["google"] = new JsonObject { ["thought_signature"] = SkipThoughtSignature },
                    };
                }

                pendingToolCalls.Add(toolCall);
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

        var body = new JsonObject { ["model"] = model, ["messages"] = messages };

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
                        function.GetProperty("arguments").GetString() ?? "{}",
                        call.TryGetProperty("extra_content", out JsonElement extra) ? extra.GetRawText() : null));
                }
            }

            string? text = StringOrNull(message, "content") ?? StringOrNull(message, "refusal");
            return new AiResponse(text, toolCalls);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new AiServiceException(AiFailure.BadResponse, "The AI service returned a response that could not be read.");
        }
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static AiServiceException MapError(HttpStatusCode status, string body)
    {
        string detail = ExtractErrorMessage(body) ?? $"HTTP {(int)status}";

        // Gemini answers an invalid key with 400 "API key not valid" rather than 401.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (status == HttpStatusCode.BadRequest && detail.Contains("API key", StringComparison.OrdinalIgnoreCase)))
        {
            return new AiServiceException(AiFailure.InvalidApiKey, detail);
        }

        return status switch
        {
            HttpStatusCode.TooManyRequests => new AiServiceException(AiFailure.RateLimited, detail),
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                => new AiServiceException(AiFailure.Busy, detail),
            _ => new AiServiceException(AiFailure.ServiceError, $"HTTP {(int)status}: {detail}"),
        };
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            // OpenAI returns { "error": {...} }; Gemini's compatibility endpoint may return [ { "error": {...} } ].
            JsonElement root = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0]
                : document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("error", out JsonElement error)
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

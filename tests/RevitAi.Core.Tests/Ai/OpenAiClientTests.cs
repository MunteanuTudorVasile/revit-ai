using System.Net;
using System.Text.Json;
using RevitAi.Core.Ai;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Ai;

public class OpenAiClientTests
{
    private const string ToolCallResponse = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                { "id": "call_1", "type": "function", "function": { "name": "get_thing", "arguments": "{\"id\":7}" } }
              ]
            },
            "finish_reason": "tool_calls"
          }]
        }
        """;

    private const string TextResponse = """
        { "choices": [{ "message": { "role": "assistant", "content": "Level 1." }, "finish_reason": "stop" }] }
        """;

    private static (OpenAiClient Client, FakeHttpHandler Handler) Create(
        HttpStatusCode status, string body, string? apiKey = "sk-test", AiProvider? provider = null)
    {
        var handler = new FakeHttpHandler(status, body);
        var connection = new AiConnection(provider ?? AiProviders.OpenAi, "test-model", apiKey);
        return (new OpenAiClient(new HttpClient(handler), () => connection), handler);
    }

    private static AiRequest Request(params AiItem[] items) =>
        new("Be helpful.", items, [new FakeTool()]);

    [Fact]
    public async Task Sends_authorised_request_with_strict_tools()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse);

        await client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None);

        Assert.Equal(AiProviders.OpenAi.Endpoint, handler.Request!.RequestUri);
        Assert.Equal("Bearer sk-test", handler.Request.Headers.Authorization!.ToString());

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("hi", root.GetProperty("messages")[1].GetProperty("content").GetString());

        JsonElement function = root.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("get_thing", function.GetProperty("name").GetString());
        Assert.True(function.GetProperty("strict").GetBoolean());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Groups_consecutive_tool_calls_into_one_assistant_message()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse);

        await client.CompleteAsync(
            Request(
                new UserMessage("hi"),
                new ToolCall("a", "get_thing", """{"id":1}"""),
                new ToolCall("b", "get_thing", """{"id":2}"""),
                new ToolOutput("a", "{}"),
                new ToolOutput("b", "{}")),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        JsonElement[] messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(2, messages[2].GetProperty("tool_calls").GetArrayLength());
        Assert.Equal("b", messages[4].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task Parses_tool_calls()
    {
        (OpenAiClient client, _) = Create(HttpStatusCode.OK, ToolCallResponse);

        AiResponse response = await client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None);

        Assert.Null(response.Text);
        Assert.Equal([new ToolCall("call_1", "get_thing", """{"id":7}""")], response.ToolCalls);
    }

    [Fact]
    public async Task Parses_text_answer()
    {
        (OpenAiClient client, _) = Create(HttpStatusCode.OK, TextResponse);

        AiResponse response = await client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None);

        Assert.Equal("Level 1.", response.Text);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Missing_key_fails_without_calling_openai()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse, apiKey: null);

        var ex = await Assert.ThrowsAsync<AiServiceException>(
            () => client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None));

        Assert.Equal(AiFailure.MissingApiKey, ex.Failure);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiFailure.InvalidApiKey)]
    [InlineData(HttpStatusCode.TooManyRequests, AiFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AiFailure.ServiceError)]
    public async Task Maps_http_errors(HttpStatusCode status, AiFailure expected)
    {
        (OpenAiClient client, _) = Create(status, """{ "error": { "message": "nope" } }""");

        var ex = await Assert.ThrowsAsync<AiServiceException>(
            () => client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Gemini_requests_go_to_googles_endpoint_with_the_gemini_key()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse, apiKey: "AIza-test", provider: AiProviders.Gemini);

        await client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None);

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer AIza-test", handler.Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Gemini_invalid_key_as_400_with_array_body_is_an_invalid_key()
    {
        (OpenAiClient client, _) = Create(HttpStatusCode.BadRequest,
            """[{ "error": { "code": 400, "message": "API key not valid. Please pass a valid API key.", "status": "INVALID_ARGUMENT" } }]""",
            provider: AiProviders.Gemini);

        var ex = await Assert.ThrowsAsync<AiServiceException>(
            () => client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None));

        Assert.Equal(AiFailure.InvalidApiKey, ex.Failure);
        Assert.Contains("API key not valid", ex.Message);
    }

    [Fact]
    public async Task Other_400_errors_are_service_errors()
    {
        (OpenAiClient client, _) = Create(HttpStatusCode.BadRequest, """{ "error": { "message": "model not found" } }""");

        var ex = await Assert.ThrowsAsync<AiServiceException>(
            () => client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None));

        Assert.Equal(AiFailure.ServiceError, ex.Failure);
        Assert.Contains("model not found", ex.Message);
    }

    [Fact]
    public void Connection_uses_the_provider_default_model_unless_overridden()
    {
        var source = new AiConnectionSource(provider => provider == "gemini" ? "AIza" : null) { Provider = AiProviders.Gemini };

        Assert.Equal((AiProviders.Gemini.DefaultModel, "AIza"), (source.Current().Model, source.Current().ApiKey));
        source.Model = " gemini-x ";
        Assert.Equal("gemini-x", source.Current().Model);
        source.Provider = AiProviders.OpenAi;
        Assert.Null(source.Current().ApiKey);
    }

    [Fact]
    public async Task Thought_signature_is_kept_and_sent_back_unchanged()
    {
        const string withSignature = """
            {
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "c1", "type": "function",
                    "function": { "name": "get_thing", "arguments": "{\"id\":1}" },
                    "extra_content": { "google": { "thought_signature": "SIG-123" } }
                  }]
                }
              }]
            }
            """;
        (OpenAiClient first, _) = Create(HttpStatusCode.OK, withSignature, provider: AiProviders.Gemini);
        ToolCall call = Assert.Single((await first.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None)).ToolCalls);

        (OpenAiClient second, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse, provider: AiProviders.Gemini);
        await second.CompleteAsync(Request(new UserMessage("hi"), call, new ToolOutput("c1", "{}")), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        JsonElement sent = body.RootElement.GetProperty("messages")[2].GetProperty("tool_calls")[0];
        Assert.Equal("SIG-123", sent.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
    }

    [Fact]
    public async Task Gemini_call_without_signature_gets_the_documented_placeholder()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse, provider: AiProviders.Gemini);

        await client.CompleteAsync(
            Request(new UserMessage("hi"), new ToolCall("c1", "get_thing", "{}"), new ToolOutput("c1", "{}")), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        string signature = body.RootElement.GetProperty("messages")[2].GetProperty("tool_calls")[0]
            .GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString()!;
        Assert.Equal("skip_thought_signature_validator", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(signature)));
    }

    [Fact]
    public async Task OpenAi_calls_carry_no_extra_content()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse);

        await client.CompleteAsync(
            Request(new UserMessage("hi"), new ToolCall("c1", "get_thing", "{}"), new ToolOutput("c1", "{}")), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.GetProperty("messages")[2].GetProperty("tool_calls")[0].TryGetProperty("extra_content", out _));
    }

    [Theory]
    [InlineData("AIzaSyExample", "gemini")]
    [InlineData(" sk-proj-abc ", "openai")]
    [InlineData("something-else", null)]
    public void Keys_are_recognised_by_prefix(string key, string? provider)
    {
        Assert.Equal(provider, AiProviders.Recognise(key)?.Id);
    }

    [Fact]
    public async Task Unreadable_response_is_reported()
    {
        (OpenAiClient client, _) = Create(HttpStatusCode.OK, """{ "unexpected": true }""");

        var ex = await Assert.ThrowsAsync<AiServiceException>(
            () => client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None));

        Assert.Equal(AiFailure.BadResponse, ex.Failure);
    }
}

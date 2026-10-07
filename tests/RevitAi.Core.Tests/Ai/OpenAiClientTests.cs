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

    private static (OpenAiClient Client, FakeHttpHandler Handler) Create(HttpStatusCode status, string body, string? apiKey = "sk-test")
    {
        var handler = new FakeHttpHandler(status, body);
        return (new OpenAiClient(new HttpClient(handler), "test-model", () => apiKey), handler);
    }

    private static AiRequest Request(params AiItem[] items) =>
        new("Be helpful.", items, [new FakeTool()]);

    [Fact]
    public async Task Sends_authorised_request_with_strict_tools()
    {
        (OpenAiClient client, FakeHttpHandler handler) = Create(HttpStatusCode.OK, TextResponse);

        await client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None);

        Assert.Equal(OpenAiClient.Endpoint, handler.Request!.RequestUri);
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
    public async Task Unreadable_response_is_reported()
    {
        (OpenAiClient client, _) = Create(HttpStatusCode.OK, """{ "unexpected": true }""");

        var ex = await Assert.ThrowsAsync<AiServiceException>(
            () => client.CompleteAsync(Request(new UserMessage("hi")), CancellationToken.None));

        Assert.Equal(AiFailure.BadResponse, ex.Failure);
    }
}

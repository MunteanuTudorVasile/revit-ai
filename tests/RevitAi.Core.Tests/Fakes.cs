using System.Net;
using System.Text.Json;
using RevitAi.Core.Ai;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests;

internal sealed class FakeTool : IReadTool
{
    private readonly Func<JsonElement, object> _execute;

    public FakeTool(
        string name = "get_thing",
        RiskLevel risk = RiskLevel.ReadOnly,
        string schema = """{ "type": "object", "properties": { "id": { "type": "integer" } }, "required": ["id"], "additionalProperties": false }""",
        Func<JsonElement, object>? execute = null)
    {
        Name = name;
        Risk = risk;
        InputSchema = ToolSchema.Parse(schema);
        _execute = execute ?? (args => new { id = args.GetProperty("id").GetInt64(), name = "Wall 1" });
    }

    public string Name { get; }

    public string Description => "Test tool.";

    public JsonElement InputSchema { get; }

    public RiskLevel Risk { get; }

    public string ProgressLabel => $"Running {Name}…";

    public List<JsonElement> Calls { get; } = [];

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        Calls.Add(arguments);
        return Task.FromResult(_execute(arguments));
    }
}

internal sealed class FakeWriteTool : IWriteTool
{
    private readonly Func<JsonElement, string> _validate;

    public FakeWriteTool(
        string name = "create_thing",
        RiskLevel risk = RiskLevel.SafeModification,
        Func<JsonElement, string>? validate = null)
    {
        Name = name;
        Risk = risk;
        _validate = validate ?? (args => $"Create thing {args.GetProperty("size").GetInt64()}");
    }

    public string Name { get; }

    public string Description => "Test write tool.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": { "size": { "type": "integer" }, "hostId": { "type": ["integer", "string", "null"] } },
          "required": ["size", "hostId"],
          "additionalProperties": false
        }
        """);

    public RiskLevel Risk { get; }

    public string ProgressLabel => $"Checking {Name}…";

    public List<JsonElement> Validations { get; } = [];

    public Task<string> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        Validations.Add(arguments);
        return Task.FromResult(_validate(arguments));
    }
}

/// <summary>Returns scripted responses in order and records every request.</summary>
internal sealed class ScriptedAiClient(params AiResponse[] responses) : IAiClient
{
    private readonly Queue<AiResponse> _responses = new(responses);

    public List<AiRequest> Requests { get; } = [];

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_responses.Dequeue());
    }

    public static AiResponse Answer(string text) => new(text, []);

    public static AiResponse Calls(params ToolCall[] calls) => new(null, calls);
}

internal sealed class FakeHttpHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

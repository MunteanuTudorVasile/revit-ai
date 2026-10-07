using RevitAi.Core.Ai;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;
using static RevitAi.Core.Tests.ScriptedAiClient;

namespace RevitAi.Core.Tests.Ai;

public sealed class OrchestratorTests : IDisposable
{
    private static readonly ModelContext Context = new("House.rvt", "Level 1", "FloorPlan", "Level 1", 1);

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());
    private readonly ToolRegistry _registry = new();
    private readonly Conversation _conversation = new();
    private readonly List<string> _progress = [];

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, recursive: true);
        }
    }

    private Orchestrator Create(IAiClient ai, int maxSteps = 8, int maxResultChars = 20_000) =>
        new(ai, _registry, new FileLog(_logDir), maxSteps, maxResultChars);

    private Task<AssistantReply> Run(Orchestrator orchestrator, string text = "What did I select?", CancellationToken ct = default) =>
        orchestrator.RunAsync(_conversation, text, Context, [], new SyncProgress(_progress), ct);

    private static string OutputOf(AiRequest request, string callId) =>
        request.Items.OfType<ToolOutput>().Single(o => o.CallId == callId).Content;

    [Fact]
    public async Task Direct_answer_is_returned_and_stored_as_one_turn()
    {
        var ai = new ScriptedAiClient(Answer("Hello."));

        AssistantReply reply = await Run(Create(ai));

        Assert.Equal("Hello.", reply.Text);
        Assert.Empty(reply.ToolCalls);
        Assert.Equal(1, _conversation.TurnCount);
        Assert.Contains("House.rvt", ai.Requests[0].Instructions);
    }

    [Fact]
    public async Task Tool_call_runs_tool_and_sends_result_back()
    {
        var tool = new FakeTool();
        _registry.Register(tool);
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": 42 }""")), Answer("It is Wall 1."));

        AssistantReply reply = await Run(Create(ai));

        Assert.Equal("It is Wall 1.", reply.Text);
        Assert.Equal(42, Assert.Single(tool.Calls).GetProperty("id").GetInt64());
        Assert.Equal("""{"id":42,"name":"Wall 1"}""", OutputOf(ai.Requests[1], "c1"));
        Assert.Equal([new ToolCallRecord("get_thing", """{ "id": 42 }""", true)], reply.ToolCalls);
        Assert.Null(reply.Plan);
        Assert.Contains("Running get_thing…", _progress);
    }

    [Fact]
    public async Task Non_ascii_text_in_results_stays_readable()
    {
        _registry.Register(new FakeTool(execute: _ => new { name = "Instalații frigorifice" }));
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": 1 }""")), Answer("Done."));

        await Run(Create(ai));

        Assert.Equal("""{"name":"Instalații frigorifice"}""", OutputOf(ai.Requests[1], "c1"));
    }

    [Fact]
    public async Task Unknown_tool_returns_error_to_ai_without_running_anything()
    {
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "delete_everything", "{}")), Answer("Sorry."));

        AssistantReply reply = await Run(Create(ai));

        Assert.Contains("Unknown tool 'delete_everything'", OutputOf(ai.Requests[1], "c1"));
        Assert.False(Assert.Single(reply.ToolCalls).Succeeded);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_the_tool_runs()
    {
        var tool = new FakeTool();
        _registry.Register(tool);
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": "abc" }""")), Answer("Retrying."));

        await Run(Create(ai));

        Assert.Empty(tool.Calls);
        Assert.Contains("Invalid arguments", OutputOf(ai.Requests[1], "c1"));
    }

    [Fact]
    public async Task Malformed_json_arguments_are_rejected()
    {
        _registry.Register(new FakeTool());
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", "{ id: ")), Answer("Retrying."));

        await Run(Create(ai));

        Assert.Contains("not valid JSON", OutputOf(ai.Requests[1], "c1"));
    }

    [Fact]
    public async Task Tool_exception_message_is_returned_to_ai()
    {
        _registry.Register(new FakeTool(execute: _ => throw new ToolException("No project is open.")));
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": 1 }""")), Answer("Open a project."));

        await Run(Create(ai));

        Assert.Equal("""{"error":"No project is open."}""", OutputOf(ai.Requests[1], "c1"));
    }

    [Fact]
    public async Task Unexpected_tool_exception_is_logged_and_returned_as_error()
    {
        _registry.Register(new FakeTool(execute: _ => throw new InvalidOperationException("boom")));
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": 1 }""")), Answer("Something failed."));

        await Run(Create(ai));

        Assert.Contains("failed unexpectedly: boom", OutputOf(ai.Requests[1], "c1"));
        Assert.Contains("boom", File.ReadAllText(Directory.GetFiles(_logDir).Single()));
    }

    [Fact]
    public async Task Timeout_from_revit_is_explained_to_ai()
    {
        _registry.Register(new FakeTool(execute: _ => throw new TimeoutException()));
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": 1 }""")), Answer("Revit is busy."));

        await Run(Create(ai));

        Assert.Contains("did not respond in time", OutputOf(ai.Requests[1], "c1"));
    }

    [Fact]
    public async Task Stops_at_step_limit_with_honest_message()
    {
        _registry.Register(new FakeTool());
        ToolCall call = new("c", "get_thing", """{ "id": 1 }""");
        var ai = new ScriptedAiClient(Calls(call), Calls(call), Calls(call));

        AssistantReply reply = await Run(Create(ai, maxSteps: 3));

        Assert.True(reply.StoppedAtStepLimit);
        Assert.StartsWith("I stopped after 3 steps", reply.Text);
        Assert.Equal(3, ai.Requests.Count);
    }

    [Fact]
    public async Task Large_results_are_truncated()
    {
        _registry.Register(new FakeTool(execute: _ => new { text = new string('x', 500) }));
        var ai = new ScriptedAiClient(Calls(new ToolCall("c1", "get_thing", """{ "id": 1 }""")), Answer("Done."));

        await Run(Create(ai, maxResultChars: 100));

        string output = OutputOf(ai.Requests[1], "c1");
        Assert.StartsWith("[Result truncated to 100 of", output);
    }

    [Fact]
    public async Task Previous_turns_are_sent_with_the_next_question()
    {
        var ai = new ScriptedAiClient(Answer("First."), Answer("Second."));
        Orchestrator orchestrator = Create(ai);

        await Run(orchestrator, "one");
        await Run(orchestrator, "two");

        Assert.Equal(
            [new UserMessage("one"), new AssistantMessage("First."), new UserMessage("two")],
            ai.Requests[1].Items);
    }

    [Fact]
    public async Task Cancelled_run_leaves_conversation_unchanged()
    {
        var ai = new ScriptedAiClient(Answer("Never."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(Create(ai), ct: new CancellationToken(canceled: true)));

        Assert.Equal(0, _conversation.TurnCount);
    }

    [Fact]
    public async Task Ai_failure_propagates_and_leaves_conversation_unchanged()
    {
        var ai = new FailingAiClient();

        await Assert.ThrowsAsync<AiServiceException>(() => Run(Create(ai)));

        Assert.Equal(0, _conversation.TurnCount);
    }

    [Fact]
    public async Task Write_tool_is_validated_and_queued_but_never_executed()
    {
        var write = new FakeWriteTool();
        _registry.Register(write);
        var ai = new ScriptedAiClient(
            Calls(new ToolCall("c1", "create_thing", """{ "size": 3, "hostId": null }""")),
            Answer("I'll create a thing. Review and click Apply."));

        AssistantReply reply = await Run(Create(ai), "make a thing");

        Assert.Single(write.Validations);
        PendingPlan plan = Assert.IsType<PendingPlan>(reply.Plan);
        Assert.Equal("make a thing", plan.UserRequest);
        PlannedOperation op = Assert.Single(plan.Operations);
        Assert.Equal((1, "create_thing", "Create thing 3"), (op.Number, op.ToolName, op.Summary));
        string output = OutputOf(ai.Requests[1], "c1");
        Assert.Contains("\"queued\":true", output);
        Assert.Contains("$op1.elementId", output);
    }

    [Fact]
    public async Task Later_operation_may_reference_earlier_one()
    {
        _registry.Register(new FakeWriteTool());
        var ai = new ScriptedAiClient(
            Calls(
                new ToolCall("c1", "create_thing", """{ "size": 1, "hostId": null }"""),
                new ToolCall("c2", "create_thing", """{ "size": 2, "hostId": "$op1.elementId" }""")),
            Answer("Planned."));

        AssistantReply reply = await Run(Create(ai));

        Assert.Equal(2, reply.Plan!.Operations.Count);
        Assert.Contains("$op1.elementId", reply.Plan.Operations[1].ArgumentsJson);
    }

    [Fact]
    public async Task Reference_to_missing_or_later_operation_is_rejected()
    {
        var write = new FakeWriteTool();
        _registry.Register(write);
        var ai = new ScriptedAiClient(
            Calls(new ToolCall("c1", "create_thing", """{ "size": 1, "hostId": "$op1.elementId" }""")),
            Answer("Hmm."));

        AssistantReply reply = await Run(Create(ai));

        Assert.Contains("does not refer to an earlier operation", OutputOf(ai.Requests[1], "c1"));
        Assert.Empty(write.Validations);
        Assert.Null(reply.Plan);
    }

    [Fact]
    public async Task Failed_validation_is_returned_to_ai_and_not_queued()
    {
        _registry.Register(new FakeWriteTool(validate: _ => throw new ToolException("Element 5 is not a level.")));
        var ai = new ScriptedAiClient(
            Calls(new ToolCall("c1", "create_thing", """{ "size": 1, "hostId": null }""")),
            Answer("That level doesn't exist."));

        AssistantReply reply = await Run(Create(ai));

        Assert.Equal("""{"error":"Element 5 is not a level."}""", OutputOf(ai.Requests[1], "c1"));
        Assert.Null(reply.Plan);
    }

    [Fact]
    public async Task Recent_actions_are_included_in_instructions()
    {
        var ai = new ScriptedAiClient(Answer("ok"));
        var plan = new PendingPlan("make wall longer");
        plan.Add("modify_wall", "{}", "Extend wall", RiskLevel.SafeModification);
        var record = new ActionRecord(
            Guid.NewGuid(), DateTimeOffset.Now, "House.rvt", "make wall longer", plan.Operations,
            new PlanRunResult(true, true, [new StepResult(1, "modify_wall", true, "Wall 18342: 4200 mm → 4700 mm", [])], [18342], "Revit AI"));

        await Create(ai).RunAsync(_conversation, "and the other one?", Context, [record], null, CancellationToken.None);

        Assert.Contains("Wall 18342: 4200 mm → 4700 mm", ai.Requests[0].Instructions);
    }

    private sealed class FailingAiClient : IAiClient
    {
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken) =>
            throw new AiServiceException(AiFailure.RateLimited, "slow down");
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need the reports immediately.</summary>
    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}

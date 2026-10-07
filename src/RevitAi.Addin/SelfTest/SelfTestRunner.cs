using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Planning;
using RevitAi.Addin.Tools;
using RevitAi.Core.Planning;
using RevitAi.Core.SelfTest;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.SelfTest;

/// <summary>A check found the wrong result.</summary>
internal sealed class SelfTestFailure(string message) : Exception(message);

/// <summary>A check cannot run in this project (e.g. no door family loaded).</summary>
internal sealed class SelfTestSkip(string message) : Exception(message);

/// <summary>
/// Runs self-test checks inside Revit's API context (ADR-045). Tools are called through the same Validate/Execute code
/// the AI path uses, with arguments checked against each tool's schema, and plans go through the real PlanExecutor.
/// The caller wraps everything in one transaction group that is rolled back.
/// </summary>
internal sealed class SelfTestRunner
{
    private readonly ToolRegistry _registry;
    private readonly PlanExecutor _executor;
    private readonly List<SelfTestResult> _results = [];
    private string _area = "";

    public SelfTestRunner(UIApplication app, ToolRegistry registry, PlanExecutor executor)
    {
        App = app;
        Document = app.ActiveUIDocument.Document;
        _registry = registry;
        _executor = executor;
    }

    public UIApplication App { get; }

    public Document Document { get; }

    public IReadOnlyList<SelfTestResult> Results => _results;

    public void Area(string area) => _area = area;

    /// <summary>Runs one check; exceptions become a failed (or skipped) result, never abort the run.</summary>
    public void Check(string name, Action body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            body();
            _results.Add(new SelfTestResult(_area, name, SelfTestOutcome.Passed, null, watch.Elapsed.TotalMilliseconds));
        }
        catch (SelfTestSkip skip)
        {
            _results.Add(new SelfTestResult(_area, name, SelfTestOutcome.Skipped, skip.Message, watch.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            string detail = ex is SelfTestFailure ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            _results.Add(new SelfTestResult(_area, name, SelfTestOutcome.Failed, detail, watch.Elapsed.TotalMilliseconds));
        }
    }

    public T Read<T>(string toolName, object arguments)
        where T : class
    {
        ITool tool = Tool(toolName);
        JsonElement json = Arguments(tool, arguments);
        object result = tool switch
        {
            RevitReadTool revit => revit.ExecuteInContext(App, json),
            IReadTool core => core.ExecuteAsync(json, CancellationToken.None).GetAwaiter().GetResult(),
            _ => throw new SelfTestFailure($"{toolName} is not a read tool."),
        };
        return result as T ?? throw new SelfTestFailure($"{toolName} returned {result.GetType().Name}, expected {typeof(T).Name}.");
    }

    /// <summary>Validates each operation like the orchestrator does, then applies the plan; any failed step fails the check.</summary>
    public PlanRunResult ApplyOk(params (string Tool, object Arguments)[] operations)
    {
        PlanRunResult result = Run(apply: true, operations);
        if (!result.Succeeded)
        {
            StepResult step = result.FailedStep!;
            throw new SelfTestFailure($"step {step.Number} ({step.ToolName}) failed: {step.Outcome}");
        }

        return result;
    }

    public PlanRunResult Run(bool apply, params (string Tool, object Arguments)[] operations)
    {
        var plan = new PendingPlan("Revit AI self-test");
        foreach ((string toolName, object arguments) in operations)
        {
            if (Tool(toolName) is not RevitWriteTool tool)
            {
                throw new SelfTestFailure($"{toolName} is not a write tool.");
            }

            JsonElement json = Arguments(tool, arguments);
            string summary = tool.ValidateInContext(Document, json);
            plan.Add(toolName, json.GetRawText(), summary, tool.Risk);
        }

        return _executor.Run(Document, plan, apply);
    }

    /// <summary>Validation only, as when the AI proposes an operation; returns the plan summary.</summary>
    public string Validate(string toolName, object arguments)
    {
        RevitWriteTool tool = Tool(toolName) as RevitWriteTool ?? throw new SelfTestFailure($"{toolName} is not a write tool.");
        return tool.ValidateInContext(Document, Arguments(tool, arguments));
    }

    /// <summary>A direct model change the tools don't offer (e.g. pinning), in its own transaction.</summary>
    public void Modify(string name, Action change)
    {
        using var transaction = new Transaction(Document, name);
        transaction.Start();
        change();
        transaction.Commit();
    }

    public static long ElementOf(PlanRunResult result, int stepNumber) =>
        result.Steps.Single(s => s.Number == stepNumber).ElementId
        ?? throw new SelfTestFailure($"step {stepNumber} reported no element.");

    public static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new SelfTestFailure(message);
        }
    }

    public static void Near(double expected, double actual, double tolerance, string what) =>
        Assert(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected:0.#}, got {actual:0.#}.");

    public static Exception Skip(string reason) => new SelfTestSkip(reason);

    private ITool Tool(string name) =>
        _registry.TryGet(name, out ITool tool) ? tool : throw new SelfTestFailure($"Tool {name} is not registered.");

    private static JsonElement Arguments(ITool tool, object arguments)
    {
        JsonElement json = JsonSerializer.SerializeToElement(arguments);
        IReadOnlyList<string> errors = SchemaValidator.Validate(tool.InputSchema, json);
        return errors.Count == 0
            ? json
            : throw new SelfTestFailure($"arguments don't match the {tool.Name} schema: {string.Join(" ", errors)}");
    }
}

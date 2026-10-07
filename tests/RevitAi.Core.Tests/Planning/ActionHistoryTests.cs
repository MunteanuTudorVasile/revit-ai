using System.Text.Json;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Planning;

namespace RevitAi.Core.Tests.Planning;

public sealed class ActionHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ActionRecord Record(string document, bool applied, string request = "make it") =>
        new(Guid.NewGuid(), DateTimeOffset.Now, document, request, [],
            new PlanRunResult(applied, applied, [], applied ? [1] : [], applied ? "Revit AI" : null));

    [Fact]
    public void Appends_one_json_line_per_record()
    {
        string path = Path.Combine(_dir, "history.jsonl");
        var history = new ActionHistory(path, new FileLog(_dir));

        history.Add(Record("A.rvt", applied: true, request: "Crează un perete"));
        history.Add(Record("A.rvt", applied: false));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Crează un perete", first.RootElement.GetProperty("userRequest").GetString());
        Assert.Contains("Crează", lines[0]);
    }

    [Fact]
    public void Recent_applied_filters_by_document_and_success()
    {
        var history = new ActionHistory(null, new FileLog(_dir));
        history.Add(Record("A.rvt", applied: true, request: "1"));
        history.Add(Record("B.rvt", applied: true, request: "2"));
        history.Add(Record("A.rvt", applied: false, request: "3"));
        history.Add(Record("A.rvt", applied: true, request: "4"));
        history.Add(Record("A.rvt", applied: true, request: "5"));

        IReadOnlyList<ActionRecord> recent = history.RecentApplied("A.rvt", 2);

        Assert.Equal(["4", "5"], recent.Select(r => r.UserRequest));
        Assert.Equal(5, history.Records.Count);
    }
}

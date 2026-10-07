using System.Text.Json;
using RevitAi.Core.Ai;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Ai;

public sealed class UnmetRequestsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());

    private string FilePath => Path.Combine(_dir, "unmet-requests.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Tool_appends_one_line_per_request()
    {
        var tool = new ReportUnavailableRequestTool(new UnmetRequestLog(FilePath, new FileLog(_dir)));
        new ToolRegistry().Register(tool);

        await tool.ExecuteAsync(ToolSchema.Parse("""{ "request": "Mută peretele spre est", "missingCapability": "rotate walls", "area": "architecture" }"""), CancellationToken.None);
        await tool.ExecuteAsync(ToolSchema.Parse("""{ "request": "Read the point cloud", "missingCapability": "point clouds", "area": "lasers" }"""), CancellationToken.None);

        string[] lines = File.ReadAllLines(FilePath);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Mută peretele spre est", first.RootElement.GetProperty("request").GetString());
        Assert.Equal("rotate walls", first.RootElement.GetProperty("missingCapability").GetString());
        Assert.Equal("architecture", first.RootElement.GetProperty("area").GetString());

        IReadOnlyList<UnmetRequest> read = UnmetRequestFile.Read(FilePath);
        Assert.Equal(["architecture", "other"], read.Select(r => r.Area)); // an unknown area is stored as "other"
    }

    [Fact]
    public void Reading_skips_damaged_lines_and_accepts_old_lines_without_area()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(FilePath,
        [
            """{"timestamp":"2026-10-01T10:00:00+03:00","request":"Add a reducer","missingCapability":"reducer"}""",
            "not json",
            """{"timestamp":"2026-10-02T10:00:00+03:00","request":"Slope the drain","missingCapability":"pipe slope","area":"piping"}""",
        ]);

        IReadOnlyList<UnmetRequest> read = UnmetRequestFile.Read(FilePath);

        Assert.Equal(2, read.Count);
        Assert.Null(read[0].Area);
        Assert.Empty(UnmetRequestFile.Read(Path.Combine(_dir, "missing.jsonl")));
    }

    [Fact]
    public void Summary_groups_by_area_and_capability_most_asked_first()
    {
        DateTimeOffset day = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(3));
        UnmetRequest[] requests =
        [
            new(day, "Add a reducer here", "Reducer between pipe sizes", "piping"),
            new(day.AddDays(1), "reduce 54 to 42", "reducer between pipe sizes ", "piping"),
            new(day.AddDays(2), "Slope | the drain", "pipe slope", "piping"),
            new(day, "Read the point cloud", "point clouds", null),
        ];

        IReadOnlyList<UnmetCapability> grouped = UnmetRequestSummary.Group(requests);
        string text = UnmetRequestSummary.Format(requests, day);

        Assert.Equal(("Reducer between pipe sizes", 2), (grouped[0].Capability, grouped[0].Count));
        Assert.Equal(["reduce 54 to 42", "Add a reducer here"], grouped[0].Examples);
        Assert.Contains("- 4 request(s), 3 different missing capabilities, from 2026-10-01 to 2026-10-03", text);
        Assert.True(text.IndexOf("## Piping (3)", StringComparison.Ordinal) < text.IndexOf("## Other (1)", StringComparison.Ordinal));
        Assert.Contains("| pipe slope | 1 | 2026-10-03 | Slope \\| the drain |", text);
    }
}

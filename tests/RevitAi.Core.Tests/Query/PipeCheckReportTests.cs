using RevitAi.Core.Query;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Query;

public class PipeCheckReportTests
{
    private static readonly ElementSummary Pipe = new(101, "Pipes", "Pipe Types", "Copper | K", 7, "Pipe", "Level 1");

    private static PipeSystemsReport Report(int openEnds) => new(
        ElementsChecked: 40,
        OpenEndCount: openEnds,
        OpenEnds: Enumerable.Repeat(new PipeIssue(Pipe, new PointMm(1000, 2000, 3000)), openEnds).ToList(),
        UnconnectedEquipmentCount: 0,
        UnconnectedEquipment: [],
        PipesWithoutSystemCount: 1,
        PipesWithoutSystem: [Pipe],
        SystemsNotWellConnected: [new SystemIssue(500, "Suction 1")],
        Truncated: false);

    [Fact]
    public void Summary_lists_every_count()
    {
        Assert.Equal(
            "2 open pipe end(s), 1 pipe(s) without a system, 1 system(s) not well connected, 0 unconnected equipment connector(s)",
            PipeCheckReport.Summary(Report(2)));
    }

    [Fact]
    public void Report_has_a_section_per_check_with_locations_and_escaped_names()
    {
        string text = PipeCheckReport.Format(Report(1), "SCAN.rvt", DateTimeOffset.Now);

        Assert.Contains("## Open pipe ends (1)", text);
        Assert.Contains("| 101 | Pipe Types: Copper \\| K | Level 1 | 1000, 2000, 3000 |", text);
        Assert.Contains("## Systems not well connected (1)", text);
        Assert.Contains("## Unconnected equipment connectors (0)", text);
        Assert.Contains("None.", text);
    }

    [Fact]
    public void Problems_are_detected()
    {
        Assert.True(PipeCheckReport.HasProblems(Report(0)));
        Assert.False(PipeCheckReport.HasProblems(Report(0) with { PipesWithoutSystemCount = 0, SystemsNotWellConnected = [] }));
    }
}

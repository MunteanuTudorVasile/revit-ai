using RevitAi.Core.Geometry;
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

    [Fact]
    public void Joint_sections_list_proposals_and_reasons()
    {
        PipeInfo[] pipes =
        [
            new(1, new Segment3(new Point3(0, 0, 0), new Point3(1900, 0, 0)), 54, 7, 9, false, true),
            new(2, new Segment3(new Point3(2000, 100, 0), new Point3(2000, 3000, 0)), 54, 7, 9, true, false),
            new(3, new Segment3(new Point3(0, 9000, 0), new Point3(2000, 9000, 0)), 54, 7, 9, false, true),
            new(4, new Segment3(new Point3(2100, 9000, 0), new Point3(4000, 9000, 0)), 42, 7, 9, true, false),
        ];
        PipeJointsReport joints = PipeJointsReport.From(PipeJoints.Find(pipes), limit: 50);

        string text = PipeCheckReport.Format(Report(0), "SCAN.rvt", DateTimeOffset.Now, joints);

        Assert.Equal("1 joint(s) can be added (1 elbow(s), 0 tee(s), 0 merge(s)), 2 place(s) need a decision", PipeCheckReport.JointSummary(joints));
        Assert.Contains("## Joints that can be added (1)", text);
        Assert.Contains("| elbow | 1 | 2 | 1900, 0, 0 |", text);
        Assert.Contains("| 3 | 4 | different diameters (needs a reducer) | 2000, 9000, 0 |", text);
    }
}

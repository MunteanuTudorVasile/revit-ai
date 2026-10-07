using RevitAi.Core.SelfTest;

namespace RevitAi.Core.Tests.SelfTest;

public class SelfTestReportTests
{
    private static readonly SelfTestResult[] Results =
    [
        new("Context", "get_project_info", SelfTestOutcome.Passed, null, 12),
        new("Modeling", "Room with walls", SelfTestOutcome.Failed, "step 5 (create_room) failed:\nnot enclosed | twice", 340),
        new("Documentation", "create_sheet", SelfTestOutcome.Skipped, "no title block loaded", 1),
    ];

    private static string Report() =>
        SelfTestReport.Format(Results, "2026", "House.rvt", new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero), "1.0.0");

    [Fact]
    public void Summary_counts_each_outcome()
    {
        Assert.Equal("1 passed, 1 failed, 1 skipped", SelfTestReport.Summary(Results));
    }

    [Fact]
    public void Failures_are_listed_first_on_one_line()
    {
        string report = Report();

        int failures = report.IndexOf("## Failures", StringComparison.Ordinal);
        int all = report.IndexOf("## All checks", StringComparison.Ordinal);
        Assert.True(failures > 0 && failures < all);
        Assert.Contains("- **Modeling / Room with walls**: step 5 (create_room) failed: not enclosed | twice", report);
    }

    [Fact]
    public void Table_escapes_pipes_and_marks_outcomes()
    {
        string report = Report();

        Assert.Contains("not enclosed \\| twice", report);
        Assert.Contains("| Documentation | create_sheet | ⏭ skipped | no title block loaded | 1 |", report);
        Assert.Contains("✅ passed", report);
    }

    [Fact]
    public void Header_states_scope_and_rollback()
    {
        string report = Report();

        Assert.Contains("Revit 2026", report);
        Assert.Contains("House.rvt", report);
        Assert.Contains("rolled back", report);
        Assert.Contains("Not covered here", report);
    }

    [Fact]
    public void No_failures_section_when_everything_passes()
    {
        string report = SelfTestReport.Format([Results[0]], "2025", "A.rvt", DateTimeOffset.Now, "1.0.0");

        Assert.DoesNotContain("## Failures", report);
    }
}

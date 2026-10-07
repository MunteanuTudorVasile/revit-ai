using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class PipeMergeTests
{
    private static Segment3 S(double x1, double y1, double z1, double x2, double y2, double z2) =>
        new(new Point3(x1, y1, z1), new Point3(x2, y2, z2));

    [Fact]
    public void Pipes_in_line_with_a_gap_merge_from_far_end_to_far_end()
    {
        MergePlan plan = PipeMerge.Plan(S(0, 0, 3000, 2000, 0, 3000), S(2200, 0, 3000, 5000, 0, 3000));

        Assert.Equal(MergeProblem.None, plan.Problem);
        Assert.Equal((0, 1), (plan.FirstFarEnd, plan.SecondFarEnd));
        Assert.Equal((new Point3(0, 0, 3000), new Point3(5000, 0, 3000)), (plan.Start, plan.End));
        Assert.Equal((5000d, 200d), (plan.LengthMm, plan.GapMm));
    }

    [Fact]
    public void Drawing_direction_does_not_matter()
    {
        MergePlan plan = PipeMerge.Plan(S(2000, 0, 0, 0, 0, 0), S(5000, 0, 0, 2200, 0, 0));

        Assert.Equal(MergeProblem.None, plan.Problem);
        Assert.Equal((1, 0), (plan.FirstFarEnd, plan.SecondFarEnd));
        Assert.Equal((new Point3(0, 0, 0), new Point3(5000, 0, 0)), (plan.Start, plan.End));
    }

    [Fact]
    public void Second_pipe_before_the_first_also_merges()
    {
        MergePlan plan = PipeMerge.Plan(S(3000, 0, 0, 5000, 0, 0), S(0, 0, 0, 2900, 0, 0));

        Assert.Equal(MergeProblem.None, plan.Problem);
        Assert.Equal(1, plan.FirstFarEnd);
        Assert.Equal((new Point3(5000, 0, 0), new Point3(0, 0, 0)), (plan.Start, plan.End));
        Assert.Equal(100, plan.GapMm);
    }

    [Fact]
    public void Touching_and_overlapping_ends_merge()
    {
        Assert.Equal(0, PipeMerge.Plan(S(0, 0, 0, 2000, 0, 0), S(2000, 0, 0, 4000, 0, 0)).GapMm);
        MergePlan overlap = PipeMerge.Plan(S(0, 0, 0, 2000, 0, 0), S(1900, 0, 0, 4000, 0, 0));
        Assert.Equal((MergeProblem.None, -100d, 4000d), (overlap.Problem, overlap.GapMm, overlap.LengthMm));
    }

    [Fact]
    public void Vertical_riser_segments_merge()
    {
        MergePlan plan = PipeMerge.Plan(S(1000, 1000, 0, 1000, 1000, 2500), S(1000, 1000, 2600, 1000, 1000, 6000));

        Assert.Equal(MergeProblem.None, plan.Problem);
        Assert.Equal(6000, plan.LengthMm);
    }

    [Fact]
    public void Pipes_at_an_angle_are_not_parallel()
    {
        Assert.Equal(MergeProblem.NotParallel, PipeMerge.Plan(S(0, 0, 0, 2000, 0, 0), S(2100, 0, 0, 4000, 100, 0)).Problem);
    }

    [Fact]
    public void Parallel_pipes_side_by_side_are_not_in_line()
    {
        MergePlan plan = PipeMerge.Plan(S(0, 0, 0, 2000, 0, 0), S(2100, 50, 0, 4000, 50, 0));

        Assert.Equal(MergeProblem.NotInLine, plan.Problem);
        Assert.Equal(50, plan.OffsetMm);
    }

    [Fact]
    public void Small_misalignment_within_tolerance_merges()
    {
        Assert.Equal(MergeProblem.None, PipeMerge.Plan(S(0, 0, 0, 2000, 0, 0), S(2100, 3, 0, 4000, 3, 0)).Problem);
    }

    [Fact]
    public void A_pipe_inside_the_other_is_refused()
    {
        Assert.Equal(MergeProblem.Contained, PipeMerge.Plan(S(0, 0, 0, 5000, 0, 0), S(1000, 0, 0, 2000, 0, 0)).Problem);
    }

    [Fact]
    public void Pipes_too_far_apart_are_refused()
    {
        Assert.Equal(MergeProblem.TooFar, PipeMerge.Plan(S(0, 0, 0, 1000, 0, 0), S(5000, 0, 0, 6000, 0, 0)).Problem);
    }
}

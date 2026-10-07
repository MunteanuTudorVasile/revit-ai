using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class PipeCornerTests
{
    private static Segment3 S(double x1, double y1, double z1, double x2, double y2, double z2) =>
        new(new Point3(x1, y1, z1), new Point3(x2, y2, z2));

    [Fact]
    public void Right_angle_with_gaps_extends_both_pipes_to_the_corner()
    {
        // Pipe A along X ending 120 mm before x = 5000; pipe B along Y starting 80 mm after y = 0.
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 3000, 4880, 0, 3000), S(5000, 80, 3000, 5000, 4000, 3000));

        Assert.Equal(CornerProblem.None, plan.Problem);
        Assert.Equal(new Point3(5000, 0, 3000), plan.Corner);
        Assert.Equal((1, 120d), (plan.FirstEnd, plan.FirstChangeMm));
        Assert.Equal((0, 80d), (plan.SecondEnd, plan.SecondChangeMm));
        Assert.Equal(90, plan.AngleDegrees);
    }

    [Fact]
    public void Overlapping_ends_within_tolerance_are_trimmed()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 0, 5010, 0, 0), S(5000, -10, 0, 5000, 3000, 0));

        Assert.Equal(CornerProblem.None, plan.Problem);
        Assert.Equal(-10, plan.FirstChangeMm);
        Assert.Equal(-10, plan.SecondChangeMm);
    }

    [Fact]
    public void Direction_of_drawing_does_not_matter()
    {
        CornerPlan plan = PipeCorner.Plan(S(4880, 0, 0, 0, 0, 0), S(5000, 4000, 0, 5000, 80, 0));

        Assert.Equal((0, 120d), (plan.FirstEnd, plan.FirstChangeMm));
        Assert.Equal((1, 80d), (plan.SecondEnd, plan.SecondChangeMm));
        Assert.Equal(90, plan.AngleDegrees);
    }

    [Fact]
    public void Forty_five_degree_corner()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 0, 1000, 0, 0), S(1000, 0, 0, 2000, 1000, 0));

        Assert.Equal(CornerProblem.None, plan.Problem);
        Assert.Equal(135, plan.AngleDegrees);
    }

    [Fact]
    public void Vertical_riser_meeting_a_horizontal_pipe_is_a_corner()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 3000, 2000, 0, 3000), S(2000, 0, 0, 2000, 0, 2900));

        Assert.Equal(CornerProblem.None, plan.Problem);
        Assert.Equal(new Point3(2000, 0, 3000), plan.Corner);
        Assert.Equal(100, plan.SecondChangeMm);
    }

    [Fact]
    public void Parallel_pipes_are_not_a_corner()
    {
        Assert.Equal(CornerProblem.Parallel, PipeCorner.Plan(S(0, 0, 0, 1000, 0, 0), S(1100, 0, 0, 2000, 0, 0)).Problem);
        Assert.Equal(CornerProblem.Parallel, PipeCorner.Plan(S(0, 0, 0, 1000, 0, 0), S(0, 500, 0, 1000, 520, 0)).Problem);
    }

    [Fact]
    public void Pipes_at_different_heights_do_not_meet()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 3000, 1000, 0, 3000), S(1000, 100, 2500, 1000, 2000, 2500));

        Assert.Equal(CornerProblem.DoNotMeet, plan.Problem);
        Assert.Equal(500, plan.OffsetMm);
    }

    [Fact]
    public void Crossing_in_the_middle_of_a_pipe_is_a_tee_not_a_corner()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 0, 4000, 0, 0), S(2000, 100, 0, 2000, 3000, 0));

        Assert.Equal(CornerProblem.MeetInMiddle, plan.Problem);
    }

    [Fact]
    public void Pipes_too_far_from_the_corner_are_refused()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 0, 1000, 0, 0), S(5000, 100, 0, 5000, 3000, 0));

        Assert.Equal(CornerProblem.TooFar, plan.Problem);
    }

    [Fact]
    public void Small_offset_within_tolerance_still_meets()
    {
        CornerPlan plan = PipeCorner.Plan(S(0, 0, 3000, 1000, 0, 3000), S(1000, 100, 3015, 1000, 2000, 3015), toleranceMm: 20);

        Assert.Equal(CornerProblem.None, plan.Problem);
        Assert.Equal(15, plan.OffsetMm);
    }
}

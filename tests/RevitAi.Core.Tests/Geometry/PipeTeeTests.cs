using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class PipeTeeTests
{
    private static Segment3 S(double x1, double y1, double z1, double x2, double y2, double z2) =>
        new(new Point3(x1, y1, z1), new Point3(x2, y2, z2));

    [Fact]
    public void Branch_short_of_the_main_is_extended_to_its_centerline()
    {
        TeePlan plan = PipeTee.Plan(S(0, 0, 3000, 4000, 0, 3000), S(2000, 100, 3000, 2000, 3000, 3000));

        Assert.Equal(TeeProblem.None, plan.Problem);
        Assert.Equal(new Point3(2000, 0, 3000), plan.Junction);
        Assert.Equal((0, 100d), (plan.BranchEnd, plan.BranchChangeMm));
    }

    [Fact]
    public void Branch_drawn_towards_the_main_moves_its_end()
    {
        TeePlan plan = PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(1500, -3000, 0, 1500, -80, 0));

        Assert.Equal(TeeProblem.None, plan.Problem);
        Assert.Equal((1, 80d), (plan.BranchEnd, plan.BranchChangeMm));
    }

    [Fact]
    public void Branch_reaching_slightly_past_the_centerline_is_trimmed()
    {
        TeePlan plan = PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(2000, 2000, 0, 2000, -10, 0));

        Assert.Equal(TeeProblem.None, plan.Problem);
        Assert.Equal(-10, plan.BranchChangeMm);
    }

    [Fact]
    public void Vertical_branch_from_a_horizontal_main()
    {
        TeePlan plan = PipeTee.Plan(S(0, 0, 3000, 4000, 0, 3000), S(1000, 0, 0, 1000, 0, 2900));

        Assert.Equal(TeeProblem.None, plan.Problem);
        Assert.Equal(new Point3(1000, 0, 3000), plan.Junction);
    }

    [Fact]
    public void Angled_branch_is_refused()
    {
        Assert.Equal(TeeProblem.NotPerpendicular, PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(2100, 100, 0, 3000, 1000, 0)).Problem);
    }

    [Fact]
    public void Branch_at_another_height_does_not_meet()
    {
        TeePlan plan = PipeTee.Plan(S(0, 0, 3000, 4000, 0, 3000), S(2000, 100, 2800, 2000, 3000, 2800));

        Assert.Equal(TeeProblem.DoNotMeet, plan.Problem);
        Assert.Equal(200, plan.OffsetMm);
    }

    [Fact]
    public void Junction_near_the_end_of_the_main_is_refused()
    {
        Assert.Equal(TeeProblem.NearMainEnd, PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(3950, 100, 0, 3950, 2000, 0)).Problem);
        Assert.Equal(TeeProblem.NearMainEnd, PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(5000, 100, 0, 5000, 2000, 0)).Problem);
    }

    [Fact]
    public void Pipes_crossing_each_other_are_refused()
    {
        Assert.Equal(TeeProblem.Crossing, PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(2000, -1000, 0, 2000, 1000, 0)).Problem);
    }

    [Fact]
    public void Branch_too_far_away_is_refused()
    {
        Assert.Equal(TeeProblem.TooFar, PipeTee.Plan(S(0, 0, 0, 4000, 0, 0), S(2000, 5000, 0, 2000, 8000, 0)).Problem);
    }
}

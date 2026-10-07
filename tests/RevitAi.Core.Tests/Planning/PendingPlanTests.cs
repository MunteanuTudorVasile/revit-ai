using RevitAi.Core.Planning;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Planning;

public class PendingPlanTests
{
    [Fact]
    public void Operations_are_numbered_from_one()
    {
        var plan = new PendingPlan("request");

        PlannedOperation first = plan.Add("create_wall", "{}", "Wall", RiskLevel.SafeModification);
        PlannedOperation second = plan.Add("create_door", "{}", "Door", RiskLevel.SafeModification);

        Assert.Equal((1, 2, 3), (first.Number, second.Number, plan.NextNumber));
    }

    [Fact]
    public void Empty_plan_is_read_only()
    {
        Assert.Equal(RiskLevel.ReadOnly, new PendingPlan("request").Risk);
    }

    [Fact]
    public void Small_safe_plan_does_not_require_preview()
    {
        var plan = new PendingPlan("request");
        for (int i = 0; i < PendingPlan.LargePlanThreshold; i++)
        {
            plan.Add("create_wall", "{}", "Wall", RiskLevel.SafeModification);
        }

        Assert.Equal(RiskLevel.SafeModification, plan.Risk);
        Assert.False(plan.RequiresPreview);
    }

    [Fact]
    public void Plan_above_threshold_escalates_to_large_and_requires_preview()
    {
        var plan = new PendingPlan("request");
        for (int i = 0; i <= PendingPlan.LargePlanThreshold; i++)
        {
            plan.Add("create_wall", "{}", "Wall", RiskLevel.SafeModification);
        }

        Assert.Equal(RiskLevel.LargeModification, plan.Risk);
        Assert.True(plan.RequiresPreview);
    }

    [Fact]
    public void Highest_operation_risk_wins()
    {
        var plan = new PendingPlan("request");
        plan.Add("create_wall", "{}", "Wall", RiskLevel.SafeModification);
        plan.Add("create_level", "{}", "Level", RiskLevel.LargeModification);

        Assert.Equal(RiskLevel.LargeModification, plan.Risk);
        Assert.True(plan.RequiresPreview);
    }
}

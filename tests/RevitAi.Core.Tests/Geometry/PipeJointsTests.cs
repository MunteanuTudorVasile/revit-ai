using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class PipeJointsTests
{
    private static PipeInfo P(long id, double x1, double y1, double x2, double y2, double diameter = 54, bool startOpen = true, bool endOpen = true) =>
        new(id, new Segment3(new Point3(x1, y1, 3000), new Point3(x2, y2, 3000)), diameter, TypeId: 7, SystemTypeId: 9, startOpen, endOpen);

    [Fact]
    public void Corner_is_an_elbow()
    {
        JointResult result = PipeJoints.Classify(P(1, 0, 0, 1900, 0), P(2, 2000, 100, 2000, 3000));

        Assert.Equal((JointKind.Elbow, 1, 0), (result.Kind, result.FirstEnd, result.SecondEnd));
    }

    [Fact]
    public void Straight_line_is_a_merge_and_different_sizes_need_a_reducer()
    {
        Assert.Equal(JointKind.Merge, PipeJoints.Classify(P(1, 0, 0, 2000, 0), P(2, 2100, 0, 4000, 0)).Kind);
        Assert.Equal(JointIssue.DifferentDiameters, PipeJoints.Classify(P(1, 0, 0, 2000, 0), P(2, 2100, 0, 4000, 0, diameter: 42)).Issue);
    }

    [Fact]
    public void Branch_into_the_middle_is_a_tee_either_way_round()
    {
        JointResult mainFirst = PipeJoints.Classify(P(1, 0, 0, 4000, 0), P(2, 2000, 100, 2000, 3000));
        JointResult branchFirst = PipeJoints.Classify(P(2, 2000, 100, 2000, 3000), P(1, 0, 0, 4000, 0));

        Assert.Equal((JointKind.Tee, true, 0), (mainFirst.Kind, mainFirst.FirstIsMain, mainFirst.SecondEnd));
        Assert.Equal((JointKind.Tee, false, 0), (branchFirst.Kind, branchFirst.FirstIsMain, branchFirst.FirstEnd));
    }

    [Fact]
    public void Reasons_for_no_joint()
    {
        Assert.Equal(JointIssue.Crossing, PipeJoints.Classify(P(1, 0, 0, 4000, 0), P(2, 2000, -1000, 2000, 1000)).Issue);
        Assert.Equal(JointIssue.AngledBranch, PipeJoints.Classify(P(1, 0, 0, 4000, 0), P(2, 2000, 0, 3000, 1000)).Issue);
        Assert.Equal(JointIssue.ParallelOffset, PipeJoints.Classify(P(1, 0, 0, 2000, 0), P(2, 2100, 30, 4000, 30)).Issue);
    }

    [Fact]
    public void Finder_proposes_one_joint_per_place()
    {
        PipeInfo[] pipes =
        [
            P(1, 0, 0, 1900, 0),                                 // corner with 2
            P(2, 2000, 100, 2000, 3000, endOpen: false),
            P(3, 0, 10_000, 2000, 10_000, startOpen: false),     // in line with 4
            P(4, 2200, 10_000, 5000, 10_000, endOpen: false),
            P(5, 0, 20_000, 4000, 20_000, startOpen: false, endOpen: false),         // main with branch 6
            P(6, 2000, 20_100, 2000, 23_000, endOpen: false),
        ];

        JointSearchResult result = PipeJoints.Find(pipes);

        Assert.Equal(3, result.Proposals.Count);
        Assert.Contains(result.Proposals, p => p.Kind == JointKind.Elbow && new[] { p.PipeId1, p.PipeId2 }.Order().SequenceEqual(new long[] { 1, 2 }));
        Assert.Contains(result.Proposals, p => p is { Kind: JointKind.Merge, PipeId1: 4, PipeId2: 3 });
        Assert.Contains(result.Proposals, p => p is { Kind: JointKind.Tee, PipeId1: 5, PipeId2: 6 });
        Assert.Empty(result.Unclear);
        Assert.Equal(1, result.LoneOpenEndCount); // pipe 1's start
    }

    [Fact]
    public void Connected_ends_are_not_proposed()
    {
        JointSearchResult result = PipeJoints.Find([P(1, 0, 0, 1900, 0, endOpen: false), P(2, 2000, 100, 2000, 3000, startOpen: false)]);

        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void A_pipe_is_used_in_only_one_proposal_and_the_rest_is_deferred()
    {
        PipeInfo[] pipes =
        [
            P(1, 0, 0, 4000, 0, startOpen: false, endOpen: false),
            P(2, 1000, 100, 1000, 3000, endOpen: false),
            P(3, 3000, 200, 3000, 3000, endOpen: false),
        ];

        JointSearchResult result = PipeJoints.Find(pipes);

        JointProposal tee = Assert.Single(result.Proposals);
        Assert.Equal(2, tee.PipeId2); // the nearer branch first
        Assert.Equal(1, result.DeferredCount);
    }

    [Fact]
    public void Unclear_places_are_listed_with_a_reason_but_racks_are_ignored()
    {
        JointSearchResult reducer = PipeJoints.Find([P(1, 0, 0, 2000, 0, startOpen: false), P(2, 2100, 0, 4000, 0, diameter: 42, endOpen: false)]);
        JointSearchResult rack = PipeJoints.Find([P(1, 0, 0, 2000, 0, startOpen: false), P(2, 0, 150, 2000, 150, startOpen: false)]);

        Assert.Equal(JointIssue.DifferentDiameters, Assert.Single(reducer.Unclear.DistinctBy(u => u.Issue)).Issue);
        Assert.Empty(rack.Unclear);
        Assert.Equal(2, rack.LoneOpenEndCount);
    }

    [Fact]
    public void Distance_to_segment_clamps_to_the_ends()
    {
        var segment = new Segment3(new Point3(0, 0, 0), new Point3(1000, 0, 0));

        Assert.Equal(100, PipeJoints.DistanceToSegment(new Point3(500, 100, 0), segment), 6);
        Assert.Equal(500, PipeJoints.DistanceToSegment(new Point3(1300, 400, 0), segment), 6);
    }
}

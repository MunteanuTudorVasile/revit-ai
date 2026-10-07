namespace RevitAi.Core.Geometry;

public enum JointKind
{
    None,
    Elbow,
    Merge,
    Tee,
}

public enum JointIssue
{
    None,
    TooShort,
    Misaligned,
    ParallelOffset,
    Overlapping,
    DifferentHeights,
    AngledBranch,
    Crossing,
    NearMainEnd,
    TooFar,
    DifferentDiameters,
    DifferentTypeOrSystem,
    EndAlreadyConnected,
}

/// <summary>A straight pipe as the joint search sees it: centerline (mm), size, type, system type and which ends are free.</summary>
public sealed record PipeInfo(long Id, Segment3 Segment, double DiameterMm, long TypeId, long SystemTypeId, bool StartOpen, bool EndOpen)
{
    public bool IsOpen(int end) => end == 0 ? StartOpen : EndOpen;

    public Point3 EndPoint(int end) => end == 0 ? Segment.Start : Segment.End;
}

/// <param name="FirstIsMain">For a tee: whether the first pipe is the main pipe (the one that is split).</param>
/// <param name="FirstEnd">End of the first pipe that takes part in the joint; -1 when none (the main pipe of a tee).</param>
/// <param name="SecondEnd">End of the second pipe that takes part in the joint; -1 when none.</param>
public sealed record JointResult(JointKind Kind, JointIssue Issue, bool FirstIsMain, int FirstEnd, int SecondEnd, double OffsetMm)
{
    public static JointResult Problem(JointIssue issue, double offset = 0) => new(JointKind.None, issue, false, -1, -1, offset);
}

/// <param name="PipeId1">Elbow: first pipe; merge: the pipe that is kept (the longer one); tee: the main pipe.</param>
/// <param name="Location">The open pipe end where the joint goes (mm).</param>
public sealed record JointProposal(JointKind Kind, long PipeId1, long PipeId2, Point3 Location, double DistanceMm);

/// <summary>An open pipe end near another pipe where no standard joint fits.</summary>
public sealed record JointUnclear(long PipeId, Point3 Location, long NearestPipeId, JointIssue Issue, double DistanceMm);

/// <param name="DeferredCount">Joints not proposed now because one of their pipes is already in another joint; search again after applying.</param>
/// <param name="LoneOpenEndCount">Open ends with no other pipe nearby (ends of runs, equipment not modelled, ...).</param>
public sealed record JointSearchResult(
    int PipesChecked,
    int OpenEndCount,
    IReadOnlyList<JointProposal> Proposals,
    IReadOnlyList<JointUnclear> Unclear,
    int DeferredCount,
    int LoneOpenEndCount);

/// <summary>
/// Decides which fitting joins two straight pipes (elbow, merge, tee) and finds the joints missing in a set of pipes,
/// deterministically and Revit-free (ADR-051). Uses the same geometry rules as the individual pipe tools.
/// </summary>
public static class PipeJoints
{
    public const double DefaultSearchMm = 500;
    private const double SizeToleranceMm = 0.01;

    public static JointResult Classify(PipeInfo first, PipeInfo second)
    {
        CornerPlan corner = PipeCorner.Plan(first.Segment, second.Segment);
        switch (corner.Problem)
        {
            case CornerProblem.None:
                return SameDiameter(first, second)
                    ? new JointResult(JointKind.Elbow, JointIssue.None, false, corner.FirstEnd, corner.SecondEnd, corner.OffsetMm)
                    : JointResult.Problem(JointIssue.DifferentDiameters, corner.OffsetMm);
            case CornerProblem.Parallel:
                return Straight(first, second);
            case CornerProblem.DoNotMeet:
                return JointResult.Problem(JointIssue.DifferentHeights, corner.OffsetMm);
            case CornerProblem.TooFar:
                return JointResult.Problem(JointIssue.TooFar, corner.OffsetMm);
            case CornerProblem.MeetInMiddle:
                return Tee(first, second);
            default:
                return JointResult.Problem(JointIssue.TooShort);
        }
    }

    /// <summary>
    /// For every open end of a pipe, looks for another pipe within <paramref name="searchMm"/> and proposes the joint that
    /// uses that end. Each pipe takes part in at most one proposal, so the proposals can be applied together.
    /// </summary>
    public static JointSearchResult Find(IReadOnlyList<PipeInfo> pipes, double searchMm = DefaultSearchMm)
    {
        var found = new Dictionary<(long, long), JointProposal>();
        var unclear = new List<JointUnclear>();
        int openEnds = 0;
        int lone = 0;

        foreach (PipeInfo pipe in pipes)
        {
            for (int end = 0; end <= 1; end++)
            {
                if (!pipe.IsOpen(end))
                {
                    continue;
                }

                openEnds++;
                Point3 point = pipe.EndPoint(end);
                var near = pipes
                    .Where(other => other.Id != pipe.Id)
                    .Select(other => (Pipe: other, Distance: DistanceToSegment(point, other.Segment)))
                    .Where(c => c.Distance <= searchMm)
                    .OrderBy(c => c.Distance)
                    .ToList();

                JointProposal? proposal = null;
                foreach ((PipeInfo other, double distance) in near)
                {
                    proposal = Propose(pipe, end, other, point, distance);
                    if (proposal is not null)
                    {
                        break;
                    }
                }

                if (proposal is not null)
                {
                    var key = (Math.Min(proposal.PipeId1, proposal.PipeId2), Math.Max(proposal.PipeId1, proposal.PipeId2));
                    if (!found.TryGetValue(key, out JointProposal? existing) || existing.DistanceMm > proposal.DistanceMm)
                    {
                        found[key] = proposal;
                    }

                    continue;
                }

                JointUnclear? reason = near.Count == 0 ? null : Unclear(pipe, end, near[0].Pipe, point, near[0].Distance);
                if (reason is null)
                {
                    lone++;
                }
                else
                {
                    unclear.Add(reason);
                }
            }
        }

        var used = new HashSet<long>();
        var accepted = new List<JointProposal>();
        int deferred = 0;
        foreach (JointProposal proposal in found.Values.OrderBy(p => p.DistanceMm).ThenBy(p => p.PipeId1))
        {
            if (used.Contains(proposal.PipeId1) || used.Contains(proposal.PipeId2))
            {
                deferred++;
                continue;
            }

            used.Add(proposal.PipeId1);
            used.Add(proposal.PipeId2);
            accepted.Add(proposal);
        }

        return new JointSearchResult(pipes.Count, openEnds, accepted, unclear, deferred, lone);
    }

    /// <summary>A joint at this open end of <paramref name="pipe"/>, when one fits and the other pipe's end there is free too.</summary>
    private static JointProposal? Propose(PipeInfo pipe, int end, PipeInfo other, Point3 point, double distance)
    {
        JointResult result = Classify(pipe, other);
        if (result.Kind == JointKind.None || result.FirstEnd != end || (result.SecondEnd >= 0 && !other.IsOpen(result.SecondEnd)))
        {
            return null;
        }

        return result.Kind switch
        {
            JointKind.Tee => new JointProposal(JointKind.Tee, other.Id, pipe.Id, point, Math.Round(distance, 1)),
            JointKind.Merge when other.Segment.Length > pipe.Segment.Length =>
                new JointProposal(JointKind.Merge, other.Id, pipe.Id, point, Math.Round(distance, 1)),
            _ => new JointProposal(result.Kind, pipe.Id, other.Id, point, Math.Round(distance, 1)),
        };
    }

    private static JointUnclear? Unclear(PipeInfo pipe, int end, PipeInfo nearest, Point3 point, double distance)
    {
        JointResult result = Classify(pipe, nearest);
        JointIssue issue = result.Kind == JointKind.None ? result.Issue : JointIssue.EndAlreadyConnected;

        // Parallel pipes side by side (e.g. in a rack) are not a missing joint.
        if (issue == JointIssue.ParallelOffset && result.OffsetMm > Math.Max(pipe.DiameterMm, nearest.DiameterMm))
        {
            return null;
        }

        return new JointUnclear(pipe.Id, point, nearest.Id, issue, Math.Round(distance, 1));
    }

    private static JointResult Straight(PipeInfo first, PipeInfo second)
    {
        MergePlan merge = PipeMerge.Plan(first.Segment, second.Segment);
        return merge.Problem switch
        {
            MergeProblem.None when !SameDiameter(first, second) => JointResult.Problem(JointIssue.DifferentDiameters, merge.OffsetMm),
            MergeProblem.None when first.TypeId != second.TypeId || first.SystemTypeId != second.SystemTypeId =>
                JointResult.Problem(JointIssue.DifferentTypeOrSystem, merge.OffsetMm),
            MergeProblem.None => new JointResult(JointKind.Merge, JointIssue.None, false, 1 - merge.FirstFarEnd, 1 - merge.SecondFarEnd, merge.OffsetMm),
            MergeProblem.NotParallel => JointResult.Problem(JointIssue.Misaligned),
            MergeProblem.NotInLine => JointResult.Problem(JointIssue.ParallelOffset, merge.OffsetMm),
            MergeProblem.Contained => JointResult.Problem(JointIssue.Overlapping, merge.OffsetMm),
            MergeProblem.TooFar => JointResult.Problem(JointIssue.TooFar, merge.OffsetMm),
            _ => JointResult.Problem(JointIssue.TooShort),
        };
    }

    private static JointResult Tee(PipeInfo first, PipeInfo second)
    {
        TeePlan firstMain = PipeTee.Plan(first.Segment, second.Segment, MinEnd(first));
        if (firstMain.Problem == TeeProblem.None)
        {
            return new JointResult(JointKind.Tee, JointIssue.None, true, -1, firstMain.BranchEnd, firstMain.OffsetMm);
        }

        TeePlan secondMain = PipeTee.Plan(second.Segment, first.Segment, MinEnd(second));
        if (secondMain.Problem == TeeProblem.None)
        {
            return new JointResult(JointKind.Tee, JointIssue.None, false, secondMain.BranchEnd, -1, secondMain.OffsetMm);
        }

        // Report the most telling reason of the two readings.
        JointIssue issue = Rank(firstMain.Problem) <= Rank(secondMain.Problem) ? Issue(firstMain.Problem) : Issue(secondMain.Problem);
        return JointResult.Problem(issue, Math.Min(firstMain.OffsetMm, secondMain.OffsetMm));
    }

    /// <summary>The tee body needs room on the main pipe: at least 150 mm or one main diameter from either end.</summary>
    public static double MinEnd(PipeInfo main) => Math.Max(PipeTee.DefaultMinEndDistanceMm, main.DiameterMm);

    // How telling a reason is (lower = reported first).
    private static int Rank(TeeProblem problem) => problem switch
    {
        TeeProblem.NotPerpendicular => 0,
        TeeProblem.NearMainEnd => 1,
        TeeProblem.DoNotMeet => 2,
        TeeProblem.TooFar => 3,
        TeeProblem.Crossing => 4,
        _ => 5,
    };

    private static JointIssue Issue(TeeProblem problem) => problem switch
    {
        TeeProblem.NotPerpendicular => JointIssue.AngledBranch,
        TeeProblem.NearMainEnd => JointIssue.NearMainEnd,
        TeeProblem.DoNotMeet => JointIssue.DifferentHeights,
        TeeProblem.TooFar => JointIssue.TooFar,
        TeeProblem.Crossing => JointIssue.Crossing,
        _ => JointIssue.TooShort,
    };

    private static bool SameDiameter(PipeInfo a, PipeInfo b) => Math.Abs(a.DiameterMm - b.DiameterMm) <= SizeToleranceMm;

    public static double DistanceToSegment(Point3 p, Segment3 segment)
    {
        Point3 d = segment.End - segment.Start;
        double lengthSquared = d.Dot(d);
        double t = lengthSquared == 0 ? 0 : Math.Clamp((p - segment.Start).Dot(d) / lengthSquared, 0, 1);
        return (p - (segment.Start + (d * t))).Length;
    }

    /// <summary>Plain-English reason for the Markdown report and the AI.</summary>
    public static string Describe(JointIssue issue) => issue switch
    {
        JointIssue.TooShort => "a pipe is too short",
        JointIssue.Misaligned => "almost in line but 1–5° off",
        JointIssue.ParallelOffset => "parallel but not in line",
        JointIssue.Overlapping => "one pipe lies inside the other",
        JointIssue.DifferentHeights => "the centerlines don't meet (different heights)",
        JointIssue.AngledBranch => "angled branch (not 90°)",
        JointIssue.Crossing => "the pipes cross each other",
        JointIssue.NearMainEnd => "branch too close to the end of the main pipe",
        JointIssue.TooFar => "too far apart",
        JointIssue.DifferentDiameters => "different diameters (needs a reducer)",
        JointIssue.DifferentTypeOrSystem => "different pipe type or system type",
        JointIssue.EndAlreadyConnected => "the other pipe's end there is already connected",
        _ => "",
    };
}

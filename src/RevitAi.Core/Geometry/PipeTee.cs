namespace RevitAi.Core.Geometry;

public enum TeeProblem
{
    None,
    TooShort,
    NotPerpendicular,
    DoNotMeet,
    NearMainEnd,
    Crossing,
    TooFar,
}

/// <param name="Junction">Where the branch centerline meets the main pipe's centerline (mm); the main pipe is split here.</param>
/// <param name="BranchEnd">Which end of the branch moves to the junction: 0 = start, 1 = end.</param>
/// <param name="BranchChangeMm">How far that end moves: positive extends, negative trims.</param>
public sealed record TeePlan(TeeProblem Problem, Point3 Junction, int BranchEnd, double BranchChangeMm, double OffsetMm);

/// <summary>
/// Works out where a branch pipe meets a main pipe for a tee, deterministically and Revit-free (ADR-050). The branch must be
/// perpendicular to the main pipe (within 5°), the centerlines must meet within the tolerance, the junction must lie inside the
/// main pipe (away from its ends) and at or beyond one end of the branch, and the branch moves at most <c>maxExtensionMm</c>.
/// </summary>
public static class PipeTee
{
    public const double DefaultToleranceMm = 20;
    public const double DefaultMaxExtensionMm = 3000;
    public const double DefaultMinEndDistanceMm = 150;
    private const double MinLengthMm = 1;
    private const double PerpendicularCosine = 0.0872; // cos 85°

    public static TeePlan Plan(
        Segment3 main,
        Segment3 branch,
        double minEndDistanceMm = DefaultMinEndDistanceMm,
        double toleranceMm = DefaultToleranceMm,
        double maxExtensionMm = DefaultMaxExtensionMm)
    {
        double lengthM = main.Length;
        double lengthB = branch.Length;
        if (lengthM < MinLengthMm || lengthB < MinLengthMm)
        {
            return Fail(TeeProblem.TooShort);
        }

        Point3 u = (main.End - main.Start) * (1 / lengthM);
        Point3 v = (branch.End - branch.Start) * (1 / lengthB);
        double cos = u.Dot(v);
        if (Math.Abs(cos) > PerpendicularCosine)
        {
            return Fail(TeeProblem.NotPerpendicular);
        }

        // Closest points of the two infinite lines: main.Start + s·u and branch.Start + t·v (u, v unit vectors).
        Point3 w = main.Start - branch.Start;
        double d = u.Dot(w);
        double e = v.Dot(w);
        double denominator = 1 - (cos * cos);
        double s = ((cos * e) - d) / denominator;
        double t = (e - (cos * d)) / denominator;
        Point3 onMain = main.Start + (u * s);
        double offset = (onMain - (branch.Start + (v * t))).Length;
        if (offset > toleranceMm)
        {
            return Fail(TeeProblem.DoNotMeet, offset);
        }

        if (s < minEndDistanceMm || s > lengthM - minEndDistanceMm)
        {
            return Fail(TeeProblem.NearMainEnd, offset);
        }

        int end = t >= lengthB / 2 ? 1 : 0;
        double change = end == 1 ? t - lengthB : -t;
        if (change < -toleranceMm)
        {
            return Fail(TeeProblem.Crossing, offset);
        }

        if (change > maxExtensionMm)
        {
            return Fail(TeeProblem.TooFar, offset);
        }

        return new TeePlan(TeeProblem.None, Round(onMain), end, Math.Round(change, 1), Math.Round(offset, 1));
    }

    private static TeePlan Fail(TeeProblem problem, double offset = 0) => new(problem, default, 0, 0, Math.Round(offset, 1));

    private static Point3 Round(Point3 p) => new(Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1));
}

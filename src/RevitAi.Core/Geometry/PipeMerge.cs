namespace RevitAi.Core.Geometry;

public enum MergeProblem
{
    None,
    TooShort,
    NotParallel,
    NotInLine,
    Contained,
    TooFar,
}

/// <param name="FirstFarEnd">End of the first pipe that stays where it is (0 = start, 1 = end).</param>
/// <param name="SecondFarEnd">End of the second pipe that becomes the merged pipe's other end.</param>
/// <param name="GapMm">Distance between the meeting ends along the line: positive = gap, negative = overlap.</param>
public sealed record MergePlan(
    MergeProblem Problem,
    int FirstFarEnd,
    int SecondFarEnd,
    Point3 Start,
    Point3 End,
    double LengthMm,
    double GapMm,
    double OffsetMm);

/// <summary>
/// Works out whether two straight pipes lie on one line and can be merged into one pipe, deterministically and Revit-free
/// (ADR-049). The merged pipe runs from the first pipe's far end to the second pipe's far end.
/// </summary>
public static class PipeMerge
{
    public const double DefaultToleranceMm = 5;
    public const double DefaultMaxGapMm = 3000;
    private const double MinLengthMm = 1;
    private const double MaxSine = 0.0175; // about 1°

    public static MergePlan Plan(
        Segment3 first,
        Segment3 second,
        double toleranceMm = DefaultToleranceMm,
        double maxGapMm = DefaultMaxGapMm)
    {
        double lengthA = first.Length;
        double lengthB = second.Length;
        if (lengthA < MinLengthMm || lengthB < MinLengthMm)
        {
            return Fail(MergeProblem.TooShort);
        }

        Point3 u = (first.End - first.Start) * (1 / lengthA);
        Point3 v = (second.End - second.Start) * (1 / lengthB);
        double cos = Math.Abs(u.Dot(v));
        if (Math.Sqrt(Math.Max(0, 1 - (cos * cos))) > MaxSine)
        {
            return Fail(MergeProblem.NotParallel);
        }

        double offset = Math.Max(DistanceToLine(second.Start, first.Start, u), DistanceToLine(second.End, first.Start, u));
        if (offset > toleranceMm)
        {
            return Fail(MergeProblem.NotInLine, offset);
        }

        // Positions along the first pipe's direction, with the first pipe's start at 0.
        double a0 = 0;
        double a1 = lengthA;
        double b0 = (second.Start - first.Start).Dot(u);
        double b1 = (second.End - first.Start).Dot(u);

        bool secondAfter = (b0 + b1) / 2 > (a0 + a1) / 2;
        int firstFar = secondAfter ? 0 : 1;
        int secondFar = secondAfter ? (b1 >= b0 ? 1 : 0) : (b1 <= b0 ? 1 : 0);
        double firstNearPos = secondAfter ? a1 : a0;
        double secondNearPos = secondFar == 1 ? b0 : b1;
        double secondFarPos = secondFar == 1 ? b1 : b0;
        double firstFarPos = secondAfter ? a0 : a1;

        double merged = Math.Abs(secondFarPos - firstFarPos);
        if (merged < Math.Max(lengthA, lengthB) - toleranceMm)
        {
            return Fail(MergeProblem.Contained, offset);
        }

        double gap = secondAfter ? secondNearPos - firstNearPos : firstNearPos - secondNearPos;
        if (gap > maxGapMm)
        {
            return Fail(MergeProblem.TooFar, offset);
        }

        Point3 start = firstFar == 0 ? first.Start : first.End;
        Point3 end = first.Start + (u * secondFarPos);
        return new MergePlan(MergeProblem.None, firstFar, secondFar, Round(start), Round(end), Math.Round(merged, 1),
            Math.Round(gap, 1), Math.Round(offset, 1));
    }

    private static double DistanceToLine(Point3 p, Point3 origin, Point3 unit)
    {
        Point3 d = p - origin;
        return (d - (unit * d.Dot(unit))).Length;
    }

    private static MergePlan Fail(MergeProblem problem, double offset = 0) =>
        new(problem, 0, 0, default, default, 0, 0, Math.Round(offset, 1));

    private static Point3 Round(Point3 p) => new(Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1));
}

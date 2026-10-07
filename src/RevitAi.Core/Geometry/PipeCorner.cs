namespace RevitAi.Core.Geometry;

public readonly record struct Point3(double X, double Y, double Z)
{
    public static Point3 operator +(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Point3 operator -(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Point3 operator *(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public double Dot(Point3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    public double Length => Math.Sqrt(Dot(this));
}

/// <summary>A straight pipe centerline from <see cref="Start"/> to <see cref="End"/>, in mm.</summary>
public readonly record struct Segment3(Point3 Start, Point3 End)
{
    public double Length => (End - Start).Length;
}

public enum CornerProblem
{
    None,
    TooShort,
    Parallel,
    DoNotMeet,
    MeetInMiddle,
    TooFar,
}

/// <param name="Corner">Where the two centerlines meet (mm).</param>
/// <param name="FirstEnd">Which end of the first pipe moves to the corner: 0 = start, 1 = end.</param>
/// <param name="FirstChangeMm">How far that end moves: positive extends, negative trims.</param>
/// <param name="AngleDegrees">Angle between the pipes' directions away from the corner (90 for a right angle).</param>
public sealed record CornerPlan(
    CornerProblem Problem,
    Point3 Corner,
    int FirstEnd,
    double FirstChangeMm,
    int SecondEnd,
    double SecondChangeMm,
    double AngleDegrees,
    double OffsetMm);

/// <summary>
/// Works out how two straight pipes meet at a corner (for an elbow), deterministically and Revit-free (ADR-047).
/// The centerlines must cross within the tolerance (same plane), the crossing must be at or beyond one end of each pipe,
/// and each pipe may be extended at most <c>maxExtensionMm</c>.
/// </summary>
public static class PipeCorner
{
    public const double DefaultToleranceMm = 20;
    public const double DefaultMaxExtensionMm = 3000;
    private const double MinLengthMm = 1;
    private const double ParallelSine = 0.0872; // sin 5°: closer to straight than this is a straight joint, not a corner

    public static CornerPlan Plan(
        Segment3 first,
        Segment3 second,
        double toleranceMm = DefaultToleranceMm,
        double maxExtensionMm = DefaultMaxExtensionMm)
    {
        double lengthA = first.Length;
        double lengthB = second.Length;
        if (lengthA < MinLengthMm || lengthB < MinLengthMm)
        {
            return Fail(CornerProblem.TooShort);
        }

        Point3 u = (first.End - first.Start) * (1 / lengthA);
        Point3 v = (second.End - second.Start) * (1 / lengthB);
        double cos = u.Dot(v);
        if (Math.Sqrt(Math.Max(0, 1 - (cos * cos))) < ParallelSine)
        {
            return Fail(CornerProblem.Parallel);
        }

        // Closest points of the two infinite lines: first.Start + s·u and second.Start + t·v (u, v unit vectors).
        Point3 w = first.Start - second.Start;
        double d = u.Dot(w);
        double e = v.Dot(w);
        double denominator = 1 - (cos * cos);
        double s = ((cos * e) - d) / denominator;
        double t = (e - (cos * d)) / denominator;
        Point3 onFirst = first.Start + (u * s);
        Point3 onSecond = second.Start + (v * t);
        double offset = (onFirst - onSecond).Length;
        if (offset > toleranceMm)
        {
            return Fail(CornerProblem.DoNotMeet, offset);
        }

        (int endA, double changeA, bool okA) = NearEnd(s, lengthA, toleranceMm);
        (int endB, double changeB, bool okB) = NearEnd(t, lengthB, toleranceMm);
        if (!okA || !okB)
        {
            return Fail(CornerProblem.MeetInMiddle, offset);
        }

        if (changeA > maxExtensionMm || changeB > maxExtensionMm)
        {
            return Fail(CornerProblem.TooFar, offset);
        }

        // Directions pointing away from the corner along each pipe.
        Point3 awayA = endA == 1 ? u * -1 : u;
        Point3 awayB = endB == 1 ? v * -1 : v;
        double angle = Math.Acos(Math.Clamp(awayA.Dot(awayB), -1, 1)) * 180 / Math.PI;

        Point3 corner = (onFirst + onSecond) * 0.5;
        return new CornerPlan(CornerProblem.None, Round(corner), endA, Math.Round(changeA, 1), endB, Math.Round(changeB, 1),
            Math.Round(angle, 1), Math.Round(offset, 1));
    }

    /// <summary>
    /// The end of a pipe nearest the crossing at parameter <paramref name="p"/> along it, and how far that end moves.
    /// Not OK when the crossing lies inside the pipe, away from both ends (a tee, not a corner).
    /// </summary>
    private static (int End, double Change, bool Ok) NearEnd(double p, double length, double tolerance)
    {
        if (p >= length / 2)
        {
            return (1, p - length, p >= length - tolerance);
        }

        return (0, -p, p <= tolerance);
    }

    private static CornerPlan Fail(CornerProblem problem, double offset = 0) =>
        new(problem, default, 0, 0, 0, 0, 0, Math.Round(offset, 1));

    private static Point3 Round(Point3 p) => new(Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1));
}

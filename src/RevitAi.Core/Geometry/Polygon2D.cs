namespace RevitAi.Core.Geometry;

public readonly record struct Point2(double X, double Y);

/// <summary>Deterministic polygon checks for boundaries (floors). Revit-free so they can be unit-tested.</summary>
public static class Polygon2D
{
    /// <summary>Absolute area by the shoelace formula, in the square of the input unit.</summary>
    public static double Area(IReadOnlyList<Point2> points)
    {
        double twice = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point2 a = points[i];
            Point2 b = points[(i + 1) % points.Count];
            twice += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(twice) / 2;
    }

    public static double MinEdgeLength(IReadOnlyList<Point2> points) =>
        Enumerable.Range(0, points.Count).Min(i => Distance(points[i], points[(i + 1) % points.Count]));

    public static double Distance(Point2 a, Point2 b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    /// <summary>True when no two non-adjacent edges touch or cross.</summary>
    public static bool IsSimple(IReadOnlyList<Point2> points)
    {
        int n = points.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                bool adjacent = j == i + 1 || (i == 0 && j == n - 1);
                if (!adjacent && SegmentsTouch(points[i], points[(i + 1) % n], points[j], points[(j + 1) % n]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SegmentsTouch(Point2 p1, Point2 p2, Point2 q1, Point2 q2)
    {
        double d1 = Cross(q1, q2, p1);
        double d2 = Cross(q1, q2, p2);
        double d3 = Cross(p1, p2, q1);
        double d4 = Cross(p1, p2, q2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        return (d1 == 0 && OnSegment(q1, q2, p1))
               || (d2 == 0 && OnSegment(q1, q2, p2))
               || (d3 == 0 && OnSegment(p1, p2, q1))
               || (d4 == 0 && OnSegment(p1, p2, q2));
    }

    private static double Cross(Point2 a, Point2 b, Point2 c) => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static bool OnSegment(Point2 a, Point2 b, Point2 p) =>
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
}

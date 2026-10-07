namespace RevitAi.Core.Geometry;

/// <summary>A suggested grid line through aligned points.</summary>
/// <param name="Axis">"vertical" (constant X, runs along Y) or "horizontal" (constant Y, runs along X).</param>
/// <param name="PositionMm">The constant coordinate: X for vertical lines, Y for horizontal lines.</param>
/// <param name="FromMm">Smallest coordinate of the member points along the line.</param>
/// <param name="ToMm">Largest coordinate of the member points along the line.</param>
public sealed record GridLineCandidate(string Axis, double PositionMm, int PointCount, double FromMm, double ToMm);

/// <param name="OffGridPointIndexes">Points not on both a vertical and a horizontal line.</param>
public sealed record GridDetectionResult(
    IReadOnlyList<GridLineCandidate> VerticalLines,
    IReadOnlyList<GridLineCandidate> HorizontalLines,
    IReadOnlyList<double> VerticalSpacingsMm,
    IReadOnlyList<double> HorizontalSpacingsMm,
    IReadOnlyList<int> OffGridPointIndexes);

/// <summary>
/// Finds rows and columns in a set of plan points (e.g. column or dot positions), for suggesting grid lines.
/// Axis-aligned to model X/Y. Points whose coordinate is within the tolerance of a line's running average join that line.
/// </summary>
public static class GridDetection
{
    public static GridDetectionResult Detect(IReadOnlyList<Point2> points, double toleranceMm, int minPointsPerLine)
    {
        if (toleranceMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), "Tolerance must be positive.");
        }

        List<(GridLineCandidate Line, List<int> Members)> vertical =
            Lines(points, p => p.X, p => p.Y, "vertical", toleranceMm, Math.Max(2, minPointsPerLine));
        List<(GridLineCandidate Line, List<int> Members)> horizontal =
            Lines(points, p => p.Y, p => p.X, "horizontal", toleranceMm, Math.Max(2, minPointsPerLine));

        var onVertical = vertical.SelectMany(l => l.Members).ToHashSet();
        var onHorizontal = horizontal.SelectMany(l => l.Members).ToHashSet();
        List<int> offGrid = Enumerable.Range(0, points.Count).Where(i => !onVertical.Contains(i) || !onHorizontal.Contains(i)).ToList();

        return new GridDetectionResult(
            vertical.Select(l => l.Line).ToList(),
            horizontal.Select(l => l.Line).ToList(),
            Spacings(vertical.Select(l => l.Line.PositionMm)),
            Spacings(horizontal.Select(l => l.Line.PositionMm)),
            offGrid);
    }

    private static List<(GridLineCandidate Line, List<int> Members)> Lines(
        IReadOnlyList<Point2> points,
        Func<Point2, double> across,
        Func<Point2, double> along,
        string axis,
        double tolerance,
        int minPoints)
    {
        var clusters = new List<List<int>>();
        List<int>? current = null;
        double mean = 0;

        foreach (int index in Enumerable.Range(0, points.Count).OrderBy(i => across(points[i])))
        {
            double value = across(points[index]);
            if (current is not null && value - mean <= tolerance)
            {
                current.Add(index);
                mean += (value - mean) / current.Count;
                continue;
            }

            current = [index];
            mean = value;
            clusters.Add(current);
        }

        return clusters
            .Where(members => members.Count >= minPoints)
            .Select(members => (
                new GridLineCandidate(
                    axis,
                    Math.Round(members.Average(i => across(points[i]))),
                    members.Count,
                    Math.Round(members.Min(i => along(points[i]))),
                    Math.Round(members.Max(i => along(points[i])))),
                members))
            .ToList();
    }

    private static List<double> Spacings(IEnumerable<double> positions)
    {
        List<double> sorted = positions.OrderBy(p => p).ToList();
        return sorted.Zip(sorted.Skip(1), (a, b) => b - a).ToList();
    }
}

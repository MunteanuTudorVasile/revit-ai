using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class GridDetectionTests
{
    /// <summary>A 3 × 4 grid (6000 mm × 5000 mm bays) with up to ±30 mm placement error.</summary>
    private static List<Point2> JitteredGrid()
    {
        double[] jitter = [0, 20, -30, 10, -15, 25, -5, 30, -20, 15, -25, 5];
        var points = new List<Point2>();
        int k = 0;
        for (int column = 0; column < 3; column++)
        {
            for (int row = 0; row < 4; row++, k++)
            {
                points.Add(new Point2((column * 6000) + jitter[k], (row * 5000) - jitter[(k + 3) % jitter.Length]));
            }
        }

        return points;
    }

    [Fact]
    public void Finds_rows_and_columns_with_spacings()
    {
        GridDetectionResult result = GridDetection.Detect(JitteredGrid(), toleranceMm: 100, minPointsPerLine: 2);

        Assert.Equal(3, result.VerticalLines.Count);
        Assert.Equal(4, result.HorizontalLines.Count);
        Assert.All(result.VerticalLines, l => Assert.Equal(4, l.PointCount));
        Assert.All(result.VerticalSpacingsMm, s => Assert.InRange(s, 5950, 6050));
        Assert.All(result.HorizontalSpacingsMm, s => Assert.InRange(s, 4950, 5050));
        Assert.Empty(result.OffGridPointIndexes);
    }

    [Fact]
    public void Line_extent_covers_its_points()
    {
        GridLineCandidate first = GridDetection.Detect(JitteredGrid(), 100, 2).VerticalLines[0];

        Assert.Equal("vertical", first.Axis);
        Assert.InRange(first.FromMm, -50, 50);
        Assert.InRange(first.ToMm, 14950, 15050);
    }

    [Fact]
    public void Stray_point_is_reported_off_grid()
    {
        List<Point2> points = JitteredGrid();
        points.Add(new Point2(3100, 2400));

        GridDetectionResult result = GridDetection.Detect(points, 100, 2);

        Assert.Equal([points.Count - 1], result.OffGridPointIndexes);
        Assert.Equal(3, result.VerticalLines.Count);
    }

    [Fact]
    public void Tolerance_uses_the_running_average_so_lines_do_not_drift()
    {
        Point2[] chain = [new(0, 0), new(40, 1000), new(80, 2000), new(120, 3000)];

        GridDetectionResult result = GridDetection.Detect(chain, toleranceMm: 50, minPointsPerLine: 2);

        Assert.Equal(2, result.VerticalLines.Count);
    }

    [Fact]
    public void Lines_with_too_few_points_are_dropped()
    {
        Point2[] points = [new(0, 0), new(0, 5000), new(6000, 0)];

        GridDetectionResult result = GridDetection.Detect(points, 100, minPointsPerLine: 2);

        Assert.Single(result.VerticalLines);
        Assert.Single(result.HorizontalLines);
        Assert.Equal([1, 2], result.OffGridPointIndexes);
    }

    [Fact]
    public void Tolerance_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GridDetection.Detect([new(0, 0)], 0, 2));
    }
}

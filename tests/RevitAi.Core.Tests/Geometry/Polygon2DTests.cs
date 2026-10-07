using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class Polygon2DTests
{
    private static readonly Point2[] Rectangle = [new(0, 0), new(5000, 0), new(5000, 4000), new(0, 4000)];

    [Fact]
    public void Area_of_rectangle_in_either_winding()
    {
        Assert.Equal(20_000_000, Polygon2D.Area(Rectangle));
        Assert.Equal(20_000_000, Polygon2D.Area(Rectangle.Reverse().ToArray()));
    }

    [Fact]
    public void Rectangle_and_l_shape_are_simple()
    {
        Point2[] lShape = [new(0, 0), new(6, 0), new(6, 2), new(2, 2), new(2, 5), new(0, 5)];

        Assert.True(Polygon2D.IsSimple(Rectangle));
        Assert.True(Polygon2D.IsSimple(lShape));
        Assert.Equal(18, Polygon2D.Area(lShape)); // 6×2 + 2×3
    }

    [Fact]
    public void Bow_tie_is_not_simple()
    {
        Point2[] bowTie = [new(0, 0), new(4, 4), new(4, 0), new(0, 4)];

        Assert.False(Polygon2D.IsSimple(bowTie));
    }

    [Fact]
    public void Polygon_touching_itself_is_not_simple()
    {
        Point2[] touching = [new(0, 0), new(4, 0), new(4, 4), new(2, 0), new(0, 4)];

        Assert.False(Polygon2D.IsSimple(touching));
    }

    [Fact]
    public void Min_edge_length_finds_shortest_edge_including_closing_edge()
    {
        Point2[] points = [new(0, 0), new(10, 0), new(10, 10), new(0, 3)];

        Assert.Equal(3, Polygon2D.MinEdgeLength(points));
    }
}

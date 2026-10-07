using RevitAi.Core.Geometry;

namespace RevitAi.Core.Tests.Geometry;

public class GridSuggestionTests
{
    /// <summary>3 columns (x = 0, 6000, 12000) × 2 rows (y = 0, 5000), exact.</summary>
    private static GridDetectionResult ThreeByTwo() =>
        GridDetection.Detect(
            [new(0, 0), new(0, 5000), new(6000, 0), new(6000, 5000), new(12000, 0), new(12000, 5000)],
            toleranceMm: 100,
            minPointsPerLine: 2);

    [Fact]
    public void Vertical_lines_get_numbers_and_horizontal_lines_get_letters_in_order()
    {
        IReadOnlyList<SuggestedGrid> grids = GridSuggestion.Suggest(ThreeByTwo(), []);

        Assert.Equal(["1", "2", "3", "A", "B"], grids.Select(g => g.Name));
        Assert.Equal([0d, 6000, 12000], grids.Take(3).Select(g => g.Start.X));
        Assert.Equal([0d, 5000], grids.Skip(3).Select(g => g.Start.Y));
    }

    [Fact]
    public void Lines_extend_past_the_outermost_points_by_the_margin()
    {
        IReadOnlyList<SuggestedGrid> grids = GridSuggestion.Suggest(ThreeByTwo(), [], marginMm: 1000);

        SuggestedGrid one = grids.Single(g => g.Name == "1");
        SuggestedGrid a = grids.Single(g => g.Name == "A");
        Assert.Equal((new Point2(0, -1000), new Point2(0, 6000)), (one.Start, one.End));
        Assert.Equal((new Point2(-1000, 0), new Point2(13000, 0)), (a.Start, a.End));
    }

    [Fact]
    public void Lines_of_one_direction_share_the_same_extent()
    {
        GridDetectionResult uneven = GridDetection.Detect(
            [new(0, 0), new(0, 9000), new(6000, 2000), new(6000, 5000)], toleranceMm: 100, minPointsPerLine: 2);

        IReadOnlyList<SuggestedGrid> vertical = GridSuggestion.Suggest(uneven, []).Where(g => g.Start.X == g.End.X).ToList();

        Assert.Equal(2, vertical.Count);
        Assert.All(vertical, g => Assert.Equal((-1000d, 10000d), (g.Start.Y, g.End.Y)));
    }

    [Fact]
    public void Existing_names_are_skipped_case_insensitively()
    {
        IReadOnlyList<SuggestedGrid> grids = GridSuggestion.Suggest(ThreeByTwo(), ["1", "3", "a"]);

        Assert.Equal(["2", "4", "5", "B", "C"], grids.Select(g => g.Name));
    }

    [Fact]
    public void Letters_skip_i_and_o_and_continue_with_two_letters()
    {
        List<string> letters = GridSuggestion.Letters().Take(26).ToList();

        Assert.DoesNotContain("I", letters);
        Assert.DoesNotContain("O", letters);
        Assert.Equal("Z", letters[23]);
        Assert.Equal(["AA", "AB"], letters.Skip(24));
    }

    [Fact]
    public void No_lines_means_no_suggestions()
    {
        GridDetectionResult none = GridDetection.Detect([new(0, 0)], 100, 2);

        Assert.Empty(GridSuggestion.Suggest(none, []));
    }
}

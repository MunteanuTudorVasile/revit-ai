namespace RevitAi.Core.Geometry;

public sealed record SuggestedGrid(string Name, Point2 Start, Point2 End);

/// <summary>
/// Turns detected grid lines into ready-to-create grids (deterministic, so the AI does not invent names or extents):
/// vertical lines (constant X, left to right) are numbered 1, 2, 3…; horizontal lines (constant Y, bottom to top) are
/// lettered A, B, C… skipping I and O, as is usual on drawings. Names already used in the project are skipped.
/// All lines of one direction get the same extent: the outermost points of the other direction plus a margin.
/// </summary>
public static class GridSuggestion
{
    public const double DefaultMarginMm = 1000;

    public static IReadOnlyList<SuggestedGrid> Suggest(
        GridDetectionResult detection,
        IEnumerable<string> existingNames,
        double marginMm = DefaultMarginMm)
    {
        var used = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var grids = new List<SuggestedGrid>();

        if (detection.VerticalLines.Count > 0)
        {
            double from = detection.VerticalLines.Min(l => l.FromMm) - marginMm;
            double to = detection.VerticalLines.Max(l => l.ToMm) + marginMm;
            IEnumerator<string> names = Numbers().Where(n => !used.Contains(n)).GetEnumerator();
            foreach (GridLineCandidate line in detection.VerticalLines.OrderBy(l => l.PositionMm))
            {
                names.MoveNext();
                grids.Add(new SuggestedGrid(names.Current, new Point2(line.PositionMm, from), new Point2(line.PositionMm, to)));
            }
        }

        if (detection.HorizontalLines.Count > 0)
        {
            double from = detection.HorizontalLines.Min(l => l.FromMm) - marginMm;
            double to = detection.HorizontalLines.Max(l => l.ToMm) + marginMm;
            IEnumerator<string> names = Letters().Where(n => !used.Contains(n)).GetEnumerator();
            foreach (GridLineCandidate line in detection.HorizontalLines.OrderBy(l => l.PositionMm))
            {
                names.MoveNext();
                grids.Add(new SuggestedGrid(names.Current, new Point2(from, line.PositionMm), new Point2(to, line.PositionMm)));
            }
        }

        return grids;
    }

    private static IEnumerable<string> Numbers()
    {
        for (int n = 1; ; n++)
        {
            yield return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A, B, … Z (without I and O), then AA, AB, … in the same alphabet.</summary>
    internal static IEnumerable<string> Letters()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        for (int length = 1; ; length++)
        {
            var indexes = new int[length];
            while (true)
            {
                yield return new string(indexes.Select(i => alphabet[i]).ToArray());

                int position = length - 1;
                while (position >= 0 && ++indexes[position] == alphabet.Length)
                {
                    indexes[position--] = 0;
                }

                if (position < 0)
                {
                    break;
                }
            }
        }
    }
}

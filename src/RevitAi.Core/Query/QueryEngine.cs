using System.Globalization;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Query;

/// <summary>A field value of one element: display text and, for numeric values, the number in tool units (mm, m², degrees, m³).</summary>
public sealed record QueryValue(string? Text, double? Number)
{
    public static QueryValue Empty { get; } = new(null, null);

    public bool IsEmpty => Number is null && string.IsNullOrWhiteSpace(Text);
}

public sealed record QueryRow(long Id, IReadOnlyDictionary<string, QueryValue> Values)
{
    public QueryValue this[string field] => Values.TryGetValue(field, out QueryValue? value) ? value : QueryValue.Empty;
}

public sealed record QueryCondition(string Field, string Op, string? Text, double? Number);

public sealed record QueryGroup(string Key, int Count, double? Sum);

public sealed record QueryResult(
    int TotalCount,
    string? GroupBy,
    IReadOnlyList<QueryGroup> Groups,
    string? SumField,
    double? Total,
    IReadOnlyList<long> ElementIds,
    bool Truncated);

/// <summary>
/// Filters, groups and sums element rows for the generic <c>query_elements</c> tool (ADR-048). Revit-free and deterministic:
/// the Revit side only reads the fields this engine says it needs.
/// </summary>
public static class QueryEngine
{
    public static readonly IReadOnlyList<string> Operators = ["equals", "notEquals", "contains", "greaterThan", "lessThan", "isEmpty", "isNotEmpty"];

    /// <summary>Pseudo-fields computed by the Revit side, independent of the Revit UI language.</summary>
    public static readonly IReadOnlyList<string> PseudoFields = ["@category", "@family", "@type", "@level", "@system", "@diameter", "@length"];

    public const string EmptyKey = "(empty)";

    public static IReadOnlyList<string> FieldsNeeded(IEnumerable<QueryCondition> conditions, string? groupBy, string? sumField) =>
        conditions.Select(c => c.Field).Append(groupBy).Append(sumField)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static QueryResult Run(IEnumerable<QueryRow> rows, IReadOnlyList<QueryCondition> conditions, string? groupBy, string? sumField, int limit)
    {
        List<QueryRow> matching = rows.Where(row => conditions.All(c => Matches(row[c.Field], c))).ToList();

        List<QueryGroup> groups = groupBy is null
            ? []
            : matching
                .GroupBy(row => Key(row[groupBy]), StringComparer.OrdinalIgnoreCase)
                .Select(g => new QueryGroup(g.Key, g.Count(), sumField is null ? null : Sum(g, sumField)))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new QueryResult(
            matching.Count,
            groupBy,
            groups,
            sumField,
            sumField is null ? null : Sum(matching, sumField),
            matching.Take(limit).Select(r => r.Id).ToList(),
            matching.Count > limit);
    }

    internal static bool Matches(QueryValue value, QueryCondition condition) => condition.Op switch
    {
        "isEmpty" => value.IsEmpty,
        "isNotEmpty" => !value.IsEmpty,
        "equals" => AreEqual(value, condition),
        "notEquals" => !AreEqual(value, condition),
        "contains" => condition.Text is { } text && (value.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false),
        "greaterThan" => value.Number is { } n && condition.Number is { } limit && n > limit,
        "lessThan" => value.Number is { } m && condition.Number is { } bound && m < bound,
        _ => throw new ToolException($"Unknown operator '{condition.Op}'. Use one of: {string.Join(", ", Operators)}."),
    };

    /// <summary>Numbers compare with a small tolerance (0.01 in tool units); text compares case-insensitively and trimmed.</summary>
    private static bool AreEqual(QueryValue value, QueryCondition condition)
    {
        if (condition.Number is { } number)
        {
            return value.Number is { } actual && Math.Abs(actual - number) <= 0.01;
        }

        if (condition.Text is null)
        {
            return value.IsEmpty;
        }

        return string.Equals(value.Text?.Trim(), condition.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(QueryValue value) =>
        !string.IsNullOrWhiteSpace(value.Text) ? value.Text.Trim()
        : value.Number is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture)
        : EmptyKey;

    private static double Sum(IEnumerable<QueryRow> rows, string field) =>
        Math.Round(rows.Select(r => r[field].Number ?? 0).Sum(), 2);
}

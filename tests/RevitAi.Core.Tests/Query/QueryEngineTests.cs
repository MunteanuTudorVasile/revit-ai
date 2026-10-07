using RevitAi.Core.Query;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Query;

public class QueryEngineTests
{
    private static QueryRow Pipe(long id, string system, double diameter, double length, string? mark = null) =>
        new(id,
            new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["@system"] = new(system, null),
                ["@diameter"] = new($"{diameter} mm", diameter),
                ["@length"] = new($"{length} mm", length),
                ["Mark"] = new(mark, null),
            });

    private static readonly QueryRow[] Pipes =
    [
        Pipe(1, "Suction", 54, 2000),
        Pipe(2, "Suction", 54, 3000, mark: "S-2"),
        Pipe(3, "Liquid", 22, 1500),
        Pipe(4, "Liquid", 28, 500),
        Pipe(5, "suction", 35, 1000),
    ];

    private static QueryCondition C(string field, string op, string? text = null, double? number = null) => new(field, op, text, number);

    [Fact]
    public void Without_conditions_everything_matches()
    {
        QueryResult result = QueryEngine.Run(Pipes, [], null, null, limit: 50);

        Assert.Equal(5, result.TotalCount);
        Assert.Empty(result.Groups);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Numeric_comparisons_use_tool_units()
    {
        QueryResult result = QueryEngine.Run(Pipes, [C("@diameter", "greaterThan", number: 30)], null, null, 50);

        Assert.Equal([1L, 2, 5], result.ElementIds);
    }

    [Fact]
    public void Text_equality_is_case_insensitive_and_groups_merge_case()
    {
        QueryResult result = QueryEngine.Run(Pipes, [C("@system", "equals", text: "SUCTION")], "@system", "@length", 50);

        Assert.Equal(3, result.TotalCount);
        QueryGroup group = Assert.Single(result.Groups);
        Assert.Equal((3, 6000d), (group.Count, group.Sum));
    }

    [Fact]
    public void Group_by_with_sum_orders_by_count()
    {
        QueryResult result = QueryEngine.Run(Pipes, [], "@system", "@length", 50);

        Assert.Equal(["Suction", "Liquid"], result.Groups.Select(g => g.Key));
        Assert.Equal([6000d, 2000d], result.Groups.Select(g => g.Sum));
        Assert.Equal(8000, result.Total);
    }

    [Fact]
    public void Empty_and_not_empty()
    {
        Assert.Equal(4, QueryEngine.Run(Pipes, [C("Mark", "isEmpty")], null, null, 50).TotalCount);
        Assert.Equal([2L], QueryEngine.Run(Pipes, [C("Mark", "isNotEmpty")], null, null, 50).ElementIds);
        Assert.Equal(5, QueryEngine.Run(Pipes, [C("No Such Field", "isEmpty")], null, null, 50).TotalCount);
    }

    [Fact]
    public void Missing_values_group_under_empty_key()
    {
        QueryResult result = QueryEngine.Run(Pipes, [], "Mark", null, 50);

        Assert.Equal(QueryEngine.EmptyKey, result.Groups[0].Key);
        Assert.Equal(4, result.Groups[0].Count);
    }

    [Fact]
    public void Number_equality_has_a_small_tolerance()
    {
        Assert.Equal(2, QueryEngine.Run(Pipes, [C("@diameter", "equals", number: 54.004)], null, null, 50).TotalCount);
        Assert.Equal(3, QueryEngine.Run(Pipes, [C("@diameter", "notEquals", number: 54)], null, null, 50).TotalCount);
    }

    [Fact]
    public void Contains_and_combined_conditions()
    {
        QueryResult result = QueryEngine.Run(
            Pipes, [C("@system", "contains", text: "uct"), C("@length", "lessThan", number: 2500)], null, null, 50);

        Assert.Equal([1L, 5], result.ElementIds);
    }

    [Fact]
    public void Results_are_limited_but_counted_in_full()
    {
        QueryResult result = QueryEngine.Run(Pipes, [], null, null, limit: 2);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.ElementIds.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Unknown_operator_is_refused()
    {
        Assert.Throws<ToolException>(() => QueryEngine.Run(Pipes, [C("@system", "startsWith", text: "S")], null, null, 50));
    }

    [Fact]
    public void Fields_needed_are_distinct_and_include_group_and_sum()
    {
        IReadOnlyList<string> fields = QueryEngine.FieldsNeeded([C("Mark", "isEmpty"), C("mark", "isNotEmpty")], "@system", "@length");

        Assert.Equal(["Mark", "@system", "@length"], fields);
    }
}

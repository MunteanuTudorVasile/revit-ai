using System.Text.Json;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Planning;

public class PlanReferencesTests
{
    private static JsonElement Json(string json) => ToolSchema.Parse(json);

    [Theory]
    [InlineData("$op1.elementId", true, 1)]
    [InlineData("$op12.elementId", true, 12)]
    [InlineData("$op.elementId", false, 0)]
    [InlineData("op1.elementId", false, 0)]
    [InlineData("$op1.elementId ", false, 0)]
    [InlineData(null, false, 0)]
    public void Parses_reference_syntax(string? value, bool ok, int number)
    {
        Assert.Equal(ok, PlanReferences.TryParse(value, out int parsed));
        Assert.Equal(number, parsed);
    }

    [Fact]
    public void Finds_references_only_in_id_properties()
    {
        JsonElement args = Json("""
            { "wallId": "$op1.elementId", "name": "$op2.elementId", "hosts": { "hostIds": ["$op3.elementId", 5] }, "levelId": 9 }
            """);

        Assert.Equal([1, 3], PlanReferences.Find(args));
    }

    [Fact]
    public void Resolves_references_to_created_ids_and_leaves_everything_else()
    {
        JsonElement args = Json("""
            { "wallId": "$op1.elementId", "name": "$op1.elementId", "hostIds": ["$op2.elementId"], "levelId": 9 }
            """);

        JsonElement resolved = PlanReferences.Resolve(args, new Dictionary<int, long> { [1] = 1001, [2] = 1002 });

        Assert.Equal(1001, resolved.GetProperty("wallId").GetInt64());
        Assert.Equal("$op1.elementId", resolved.GetProperty("name").GetString());
        Assert.Equal(1002, resolved.GetProperty("hostIds")[0].GetInt64());
        Assert.Equal(9, resolved.GetProperty("levelId").GetInt64());
    }

    [Fact]
    public void Unresolvable_reference_throws_tool_exception()
    {
        var ex = Assert.Throws<ToolException>(
            () => PlanReferences.Resolve(Json("""{ "wallId": "$op4.elementId" }"""), new Dictionary<int, long>()));

        Assert.Contains("$op4.elementId", ex.Message);
    }
}

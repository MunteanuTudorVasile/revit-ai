using System.Text.Json;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Tools;

public class SchemaValidatorTests
{
    private static readonly JsonElement FindSchema = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name" },
            "levelId": { "type": ["integer", "null"] },
            "names": { "type": ["array", "null"], "items": { "type": "string" } },
            "mode": { "type": "string", "enum": ["fast", "full"] }
          },
          "required": ["category", "levelId", "names", "mode"],
          "additionalProperties": false
        }
        """);

    private static IReadOnlyList<string> Validate(string json) => SchemaValidator.Validate(FindSchema, Json(json));

    private static JsonElement Json(string json) => ToolSchema.Parse(json);

    [Fact]
    public void Valid_arguments_pass()
    {
        Assert.Empty(Validate("""{ "category": "Walls", "levelId": 12, "names": ["a"], "mode": "fast" }"""));
        Assert.Empty(Validate("""{ "category": "Walls", "levelId": null, "names": null, "mode": "full" }"""));
    }

    [Fact]
    public void Missing_required_property_fails()
    {
        IReadOnlyList<string> errors = Validate("""{ "category": "Walls", "names": null, "mode": "fast" }""");

        Assert.Equal(["$.levelId: is required."], errors);
    }

    [Fact]
    public void Unknown_property_fails_when_additional_properties_false()
    {
        IReadOnlyList<string> errors = Validate("""{ "category": "Walls", "levelId": null, "names": null, "mode": "fast", "x": 1 }""");

        Assert.Equal(["$.x: is not an allowed property."], errors);
    }

    [Fact]
    public void Wrong_types_fail_with_paths()
    {
        IReadOnlyList<string> errors = Validate("""{ "category": 5, "levelId": 1.5, "names": ["a", 2], "mode": "fast" }""");

        Assert.Contains("$.category: expected string, got integer.", errors);
        Assert.Contains("$.levelId: expected integer or null, got number.", errors);
        Assert.Contains("$.names[1]: expected string, got integer.", errors);
    }

    [Fact]
    public void Value_outside_enum_fails()
    {
        IReadOnlyList<string> errors = Validate("""{ "category": "Walls", "levelId": null, "names": null, "mode": "slow" }""");

        Assert.Single(errors);
        Assert.StartsWith("$.mode: must be one of", errors[0]);
    }

    [Fact]
    public void Non_object_root_fails()
    {
        Assert.Equal(["$: expected object, got array."], Validate("[]"));
    }
}

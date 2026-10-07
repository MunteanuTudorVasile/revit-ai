using System.Text.Json;

namespace RevitAi.Core.Tools;

/// <summary>
/// Validates AI-supplied arguments against a tool's schema before the tool runs.
/// Supports the subset used by our tools (and accepted by OpenAI strict mode):
/// type (single or array, incl. "null"), properties, required, additionalProperties: false, items, enum.
/// Unknown keywords such as "description" are ignored.
/// </summary>
public static class SchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement value)
    {
        var errors = new List<string>();
        Check(schema, value, "$", errors);
        return errors;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(type, value))
        {
            errors.Add($"{path}: expected {DescribeType(type)}, got {KindName(value)}.");
            return;
        }

        if (schema.TryGetProperty("enum", out JsonElement allowed)
            && !allowed.EnumerateArray().Any(option => option.ValueKind == value.ValueKind && option.ToString() == value.ToString()))
        {
            errors.Add($"{path}: must be one of {allowed.GetRawText()}.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            CheckObject(schema, value, path, errors);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                Check(items, item, $"{path}[{index++}]", errors);
            }
        }
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties);

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (!value.TryGetProperty(name.GetString()!, out _))
                {
                    errors.Add($"{path}.{name.GetString()}: is required.");
                }
            }
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
            {
                Check(propertySchema, property.Value, $"{path}.{property.Name}", errors);
            }
            else if (schema.TryGetProperty("additionalProperties", out JsonElement additional)
                     && additional.ValueKind == JsonValueKind.False)
            {
                errors.Add($"{path}.{property.Name}: is not an allowed property.");
            }
        }
    }

    private static bool MatchesType(JsonElement type, JsonElement value) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => MatchesType(t.GetString()!, value))
            : MatchesType(type.GetString()!, value);

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string DescribeType(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? string.Join(" or ", type.EnumerateArray().Select(t => t.GetString()))
            : type.GetString()!;

    private static string KindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };
}

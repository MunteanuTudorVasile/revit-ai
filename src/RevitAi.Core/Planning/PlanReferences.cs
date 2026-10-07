using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Planning;

/// <summary>
/// References to elements created earlier in the same plan, written as <c>"$opN.elementId"</c> (ADR-024).
/// Only properties whose name ends in "Id" or "Ids" are treated as references, so free text such as a room name
/// is never rewritten.
/// </summary>
public static partial class PlanReferences
{
    public static string For(int operationNumber) => $"$op{operationNumber}.elementId";

    public static bool TryParse(string? value, out int operationNumber)
    {
        Match match = value is null ? Match.Empty : ReferencePattern().Match(value);
        operationNumber = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return match.Success;
    }

    /// <summary>All operation numbers referenced anywhere in the arguments.</summary>
    public static IReadOnlyList<int> Find(JsonElement arguments)
    {
        var found = new List<int>();
        Walk(arguments, null, found);
        return found;
    }

    /// <summary>Replaces each reference with the ID created by that operation.</summary>
    /// <exception cref="ToolException">A referenced operation has not created an element.</exception>
    public static JsonElement Resolve(JsonElement arguments, IReadOnlyDictionary<int, long> createdIds)
    {
        JsonNode? root = JsonNode.Parse(arguments.GetRawText());
        ResolveNode(root, null, createdIds);
        return ToolSchema.Parse(root?.ToJsonString() ?? "null");
    }

    private static void Walk(JsonElement element, string? propertyName, List<int> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, property.Name, found);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, propertyName, found);
                }

                break;
            case JsonValueKind.String when IsIdProperty(propertyName) && TryParse(element.GetString(), out int number):
                found.Add(number);
                break;
        }
    }

    private static void ResolveNode(JsonNode? node, string? propertyName, IReadOnlyDictionary<int, long> createdIds)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToList())
                {
                    if (IsReference(property.Key, property.Value, out int number))
                    {
                        obj[property.Key] = Lookup(number, createdIds);
                    }
                    else
                    {
                        ResolveNode(property.Value, property.Key, createdIds);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (IsReference(propertyName, array[i], out int number))
                    {
                        array[i] = Lookup(number, createdIds);
                    }
                    else
                    {
                        ResolveNode(array[i], propertyName, createdIds);
                    }
                }

                break;
        }
    }

    private static bool IsReference(string? propertyName, JsonNode? node, out int number)
    {
        number = 0;
        return IsIdProperty(propertyName)
               && node is JsonValue value
               && value.TryGetValue(out string? text)
               && TryParse(text, out number);
    }

    private static JsonNode Lookup(int number, IReadOnlyDictionary<int, long> createdIds) =>
        createdIds.TryGetValue(number, out long id)
            ? JsonValue.Create(id)
            : throw new ToolException($"{For(number)} does not refer to an element created earlier in this plan.");

    private static bool IsIdProperty(string? name) =>
        name is not null && (name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Ids", StringComparison.Ordinal));

    [GeneratedRegex(@"^\$op(\d{1,4})\.elementId$")]
    private static partial Regex ReferencePattern();
}

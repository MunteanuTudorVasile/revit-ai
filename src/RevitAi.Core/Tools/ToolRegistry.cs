using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitAi.Core.Tools;

/// <summary>The approved tools. The AI can call nothing that is not registered here (ADR-005, ADR-019).</summary>
public sealed partial class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    public void Register(ITool tool)
    {
        if (!NamePattern().IsMatch(tool.Name))
        {
            throw new ArgumentException($"Tool name '{tool.Name}' must be snake_case.", nameof(tool));
        }

        switch (tool)
        {
            case IReadTool when tool.Risk != RiskLevel.ReadOnly:
                throw new ArgumentException($"Read tool '{tool.Name}' must be READ_ONLY.", nameof(tool));
            case IWriteTool when tool.Risk == RiskLevel.ReadOnly:
                throw new ArgumentException($"Write tool '{tool.Name}' cannot be READ_ONLY.", nameof(tool));
            case IReadTool or IWriteTool:
                break;
            default:
                throw new ArgumentException($"Tool '{tool.Name}' must be a read tool or a write tool.", nameof(tool));
        }

        if (tool.InputSchema.ValueKind != JsonValueKind.Object
            || !tool.InputSchema.TryGetProperty("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "object")
        {
            throw new ArgumentException($"Tool '{tool.Name}' input schema must be a JSON object schema.", nameof(tool));
        }

        if (!_tools.TryAdd(tool.Name, tool))
        {
            throw new ArgumentException($"Tool '{tool.Name}' is already registered.", nameof(tool));
        }
    }

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();
}

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Query;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Generic query and pipe-system integrity check (ADR-048). Both read-only.

/// <summary>Reads one query field of an element: a parameter by name, or one of the language-independent @ pseudo-fields.</summary>
internal static class FieldReader
{
    private static readonly ForgeTypeId[] ToolUnits =
        [UnitTypeId.Millimeters, UnitTypeId.SquareMeters, UnitTypeId.Degrees, UnitTypeId.CubicMeters];

    /// <returns>The value, and whether the field exists on this element.</returns>
    public static (QueryValue Value, bool Exists) Read(Document document, Element element, string field)
    {
        switch (field.ToLowerInvariant())
        {
            case "@category":
                return (Text(element.Category?.Name), true);
            case "@family":
                return (Text((document.GetElement(element.GetTypeId()) as ElementType)?.FamilyName), true);
            case "@type":
                return (Text((document.GetElement(element.GetTypeId()) as ElementType)?.Name), true);
            case "@level":
                return (Text(element.LevelId == ElementId.InvalidElementId ? null : document.GetElement(element.LevelId)?.Name), true);
            case "@system":
                return (Param(element.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)), true);
            case "@diameter":
                return (Param(element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                              ?? element.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)), true);
            case "@length":
                return element.Location is LocationCurve curve
                    ? (Number(UnitUtils.ConvertFromInternalUnits(curve.Curve.Length, UnitTypeId.Millimeters)), true)
                    : (Param(element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)), true);
        }

        Parameter? parameter = element.LookupParameter(field) ?? document.GetElement(element.GetTypeId())?.LookupParameter(field);
        return (Param(parameter), parameter is not null);
    }

    private static QueryValue Text(string? text) => new(text, null);

    private static QueryValue Number(double value) => new($"{value:0.#} mm", Math.Round(value, 2));

    private static QueryValue Param(Parameter? parameter)
    {
        if (parameter is null || !parameter.HasValue)
        {
            return QueryValue.Empty;
        }

        return parameter.StorageType switch
        {
            StorageType.String => new QueryValue(parameter.AsString(), null),
            StorageType.Integer => new QueryValue(parameter.AsValueString(), parameter.AsInteger()),
            StorageType.Double => new QueryValue(parameter.AsValueString(), ToolNumber(parameter)),
            _ => new QueryValue(parameter.AsValueString(), null),
        };
    }

    /// <summary>The value in tool units (mm, m², degrees, m³) when the spec accepts one of them; otherwise the raw value.</summary>
    private static double ToolNumber(Parameter parameter)
    {
        ForgeTypeId spec = parameter.Definition.GetDataType();
        double value = parameter.AsDouble();
        if (!UnitUtils.IsMeasurableSpec(spec))
        {
            return Math.Round(value, 4);
        }

        IList<ForgeTypeId> valid = UnitUtils.GetValidUnits(spec);
        ForgeTypeId? unit = ToolUnits.FirstOrDefault(valid.Contains);
        return Math.Round(unit is null ? value : UnitUtils.ConvertFromInternalUnits(value, unit), 3);
    }
}

public sealed class QueryElementsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public override string Name => "query_elements";

    public override string Description =>
        "Generic question tool: elements of one category, optionally on one level, filtered by conditions, optionally grouped " +
        "and summed. Fields are parameter names as shown in the Properties palette (instance first, then type), or the " +
        "language-independent fields @category, @family, @type, @level, @system, @diameter, @length. Numbers are in mm, m², " +
        "degrees or m³. Operators: equals, notEquals, contains, greaterThan, lessThan, isEmpty, isNotEmpty. Returns the total " +
        "count, groups (key, count, sum), the overall sum, up to 'limit' elements, and fields not found on any element. " +
        "Examples: pipes over 50 mm per system with total length; doors without a Mark; rooms per level with total area.";

    public override string ProgressLabel => "Querying the model…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category as shown in Revit, e.g. Pipes, Pipe Fittings, Doors, Rooms." },
            "levelId": { "type": ["integer", "null"], "description": "Only elements on this level. Null for all." },
            "conditions": {
              "type": ["array", "null"],
              "items": {
                "type": "object",
                "properties": {
                  "field": { "type": "string" },
                  "op": { "type": "string", "enum": ["equals", "notEquals", "contains", "greaterThan", "lessThan", "isEmpty", "isNotEmpty"] },
                  "value": { "type": ["string", "number", "null"] }
                },
                "required": ["field", "op", "value"],
                "additionalProperties": false
              },
              "description": "All conditions must hold. Null or empty for none."
            },
            "groupBy": { "type": ["string", "null"], "description": "Field to group by, e.g. @system, @type, @level. Null for no grouping." },
            "sumField": { "type": ["string", "null"], "description": "Numeric field to sum, e.g. @length. Null for none." },
            "limit": { "type": ["integer", "null"], "description": "Maximum elements listed. Null for 50." }
          },
          "required": ["category", "levelId", "conditions", "groupBy", "sumField", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        Category category = RevitRead.RequireCategory(document, arguments.GetProperty("category").GetString()!);
        long? levelId = RevitRead.OptionalLong(arguments, "levelId");
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        string? groupBy = RevitRead.OptionalString(arguments, "groupBy");
        string? sumField = RevitRead.OptionalString(arguments, "sumField");

        List<QueryCondition> conditions = arguments.GetProperty("conditions").ValueKind == JsonValueKind.Array
            ? arguments.GetProperty("conditions").EnumerateArray().Select(c => new QueryCondition(
                c.GetProperty("field").GetString()!,
                c.GetProperty("op").GetString()!,
                c.GetProperty("value").ValueKind == JsonValueKind.String ? c.GetProperty("value").GetString() : null,
                c.GetProperty("value").ValueKind == JsonValueKind.Number ? c.GetProperty("value").GetDouble() : null)).ToList()
            : [];

        IReadOnlyList<string> fields = QueryEngine.FieldsNeeded(conditions, groupBy, sumField);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<QueryRow>();
        foreach (Element element in new FilteredElementCollector(document).OfCategoryId(category.Id).WhereElementIsNotElementType())
        {
            if (levelId is not null && element.LevelId.Value != levelId)
            {
                continue;
            }

            var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in fields)
            {
                (QueryValue value, bool exists) = FieldReader.Read(document, element, field);
                values[field] = value;
                if (exists)
                {
                    found.Add(field);
                }
            }

            rows.Add(new QueryRow(element.Id.Value, values));
        }

        QueryResult result = QueryEngine.Run(rows, conditions, groupBy, sumField, limit);
        return new QueryElementsResult(
            category.Name,
            result.TotalCount,
            result.GroupBy,
            result.Groups,
            result.SumField,
            result.Total,
            result.ElementIds.Select(id => RevitRead.Summarize(document.GetElement(new ElementId(id)))).ToList(),
            result.Truncated,
            rows.Count == 0 ? [] : fields.Where(f => !found.Contains(f)).ToList());
    }
}

public sealed class CheckPipeSystemsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private static readonly BuiltInCategory[] PipingCategories =
    [
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
    ];

    private static readonly BuiltInCategory[] EquipmentCategories =
        [BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures];

    public override string Name => "check_pipe_systems";

    public override string Description =>
        "Pipe-system integrity report (read-only): open pipe ends (unconnected connectors of pipes, fittings and accessories), " +
        "unconnected piping connectors on equipment and fixtures, pipes without a system, and piping systems Revit reports as " +
        "not well connected. Scope: the given elements, or a level, or (both null) the whole model.";

    public override string ProgressLabel => "Checking the pipe systems…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": ["array", "null"], "items": { "type": "integer" }, "description": "Only these elements. Null for a level or the whole model." },
            "levelId": { "type": ["integer", "null"], "description": "Only elements on this level. Null for all." },
            "limit": { "type": ["integer", "null"], "description": "Maximum items listed per check. Null for 50." }
          },
          "required": ["elementIds", "levelId", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        long? levelId = RevitRead.OptionalLong(arguments, "levelId");
        HashSet<long>? only = arguments.GetProperty("elementIds").ValueKind == JsonValueKind.Array
            ? arguments.GetProperty("elementIds").EnumerateArray().Select(id => id.GetInt64()).ToHashSet()
            : null;

        bool InScope(Element e) => (only is null || only.Contains(e.Id.Value)) && (levelId is null || e.LevelId.Value == levelId);

        List<Element> piping = Collect(document, PipingCategories).Where(InScope).ToList();
        List<Element> equipment = Collect(document, EquipmentCategories).Where(InScope).ToList();

        List<PipeIssue> openEnds = OpenConnectors(piping).ToList();
        List<PipeIssue> unconnectedEquipment = OpenConnectors(equipment).ToList();
        List<Element> withoutSystem = piping.Where(e => e is Pipe { MEPSystem: null }).ToList();
        List<SystemIssue> notWellConnected = new FilteredElementCollector(document)
            .OfClass(typeof(PipingSystem))
            .Cast<PipingSystem>()
            .Where(s => !s.IsWellConnected && (only is null || s.PipingNetwork.Cast<Element>().Any(e => only.Contains(e.Id.Value))))
            .Select(s => new SystemIssue(s.Id.Value, s.Name))
            .ToList();

        return new PipeSystemsReport(
            piping.Count + equipment.Count,
            openEnds.Count,
            openEnds.Take(limit).ToList(),
            unconnectedEquipment.Count,
            unconnectedEquipment.Take(limit).ToList(),
            withoutSystem.Count,
            withoutSystem.Take(limit).Select(RevitRead.Summarize).ToList(),
            notWellConnected.Take(limit).ToList(),
            openEnds.Count > limit || unconnectedEquipment.Count > limit || withoutSystem.Count > limit || notWellConnected.Count > limit);
    }

    private static IEnumerable<Element> Collect(Document document, IEnumerable<BuiltInCategory> categories) =>
        categories.SelectMany(c => new FilteredElementCollector(document).OfCategory(c).WhereElementIsNotElementType());

    private static IEnumerable<PipeIssue> OpenConnectors(IEnumerable<Element> elements)
    {
        foreach (Element element in elements)
        {
            ConnectorManager? manager = element switch
            {
                MEPCurve curve => curve.ConnectorManager,
                FamilyInstance instance => instance.MEPModel?.ConnectorManager,
                _ => null,
            };
            if (manager is null)
            {
                continue;
            }

            foreach (Connector connector in manager.Connectors)
            {
                if (connector.Domain == Domain.DomainPiping && connector.ConnectorType == ConnectorType.End && !connector.IsConnected)
                {
                    yield return new PipeIssue(RevitRead.Summarize(element), RevitRead.Point(connector.Origin));
                }
            }
        }
    }
}

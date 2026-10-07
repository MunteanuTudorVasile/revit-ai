using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

public sealed class GetSelectedElementsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int MaxElements = 50;

    public override string Name => "get_selected_elements";

    public override string Description =>
        $"Returns the elements the user has selected in Revit (up to {MaxElements}): ID, category, family, type, name and level.";

    public override string ProgressLabel => "Reading your selection…";

    protected override string SchemaJson => NoArguments;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        UIDocument uiDocument = RevitRead.RequireUiDocument(app);
        ICollection<ElementId> ids = uiDocument.Selection.GetElementIds();
        List<ElementSummary> elements = ids
            .Take(MaxElements)
            .Select(uiDocument.Document.GetElement)
            .Where(element => element is not null)
            .Select(RevitRead.Summarize)
            .ToList();
        return new ElementListResult(ids.Count, ids.Count > MaxElements, elements);
    }
}

public sealed class GetElementTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "get_element";

    public override string Description =>
        "Returns one element by ID: category, family, type, name, level and location. " +
        "Location is a point or a curve (start, end, length) in model coordinates, in mm.";

    public override string ProgressLabel => "Reading element…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementId": { "type": "integer", "description": "Element ID taken from another tool result." }
          },
          "required": ["elementId"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Element element = RevitRead.RequireElement(RevitRead.RequireDocument(app), arguments.GetProperty("elementId").GetInt64());
        LocationResult? location = element.Location switch
        {
            LocationPoint point => new LocationResult("point", RevitRead.Point(point.Point), null, null, null),
            LocationCurve curve => new LocationResult(
                "curve",
                null,
                RevitRead.Point(curve.Curve.GetEndPoint(0)),
                RevitRead.Point(curve.Curve.GetEndPoint(1)),
                RevitRead.Mm(curve.Curve.Length)),
            _ => null,
        };
        return new ElementDetails(RevitRead.Summarize(element), location);
    }
}

public sealed class FindElementsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public override string Name => "find_elements";

    public override string Description =>
        "Finds model elements (not types) of one category, optionally on one level and/or whose name contains some text. " +
        $"Returns the total count and up to 'limit' elements (default {DefaultLimit}, max {MaxLimit}).";

    public override string ProgressLabel => "Searching the model…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name as shown in Revit, e.g. Walls, Doors, Windows, Rooms, Floors." },
            "levelId": { "type": ["integer", "null"], "description": "Only elements on this level. Null for all levels." },
            "nameContains": { "type": ["string", "null"], "description": "Case-insensitive text the element name must contain. Null for any name." },
            "limit": { "type": ["integer", "null"], "description": "Maximum elements to return. Null for the default." }
          },
          "required": ["category", "levelId", "nameContains", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        string categoryName = arguments.GetProperty("category").GetString()!;
        Category category = document.Settings.Categories
            .Cast<Category>()
            .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"No category named '{categoryName}'. Use the category name as shown in Revit, e.g. Walls, Doors, Rooms.");

        long? levelId = RevitRead.OptionalLong(arguments, "levelId");
        string? nameContains = RevitRead.OptionalString(arguments, "nameContains");
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);

        List<Element> matches = new FilteredElementCollector(document)
            .OfCategoryId(category.Id)
            .WhereElementIsNotElementType()
            .Where(e => levelId is null || e.LevelId.Value == levelId)
            .Where(e => nameContains is null || e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new ElementListResult(matches.Count, matches.Count > limit, matches.Take(limit).Select(RevitRead.Summarize).ToList());
    }
}

public sealed class GetElementParametersTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int MaxParameters = 80;

    public override string Name => "get_element_parameters";

    public override string Description =>
        "Returns parameter values of one element. Named parameters are looked up on the element first, then on its type. " +
        $"With parameterNames null, returns the element's instance parameters (up to {MaxParameters}). " +
        "Each value has the display text; length values also have valueMm and area values valueM2.";

    public override string ProgressLabel => "Reading parameters…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementId": { "type": "integer", "description": "Element ID taken from another tool result." },
            "parameterNames": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Parameter names as shown in Revit's Properties palette, e.g. Length, Base Constraint. Null for all instance parameters."
            }
          },
          "required": ["elementId", "parameterNames"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        long elementId = arguments.GetProperty("elementId").GetInt64();
        Element element = RevitRead.RequireElement(document, elementId);

        if (arguments.GetProperty("parameterNames").ValueKind != JsonValueKind.Array)
        {
            List<Parameter> all = element.Parameters.Cast<Parameter>().OrderBy(p => p.Definition.Name).ToList();
            return new ParametersResult(
                elementId,
                all.Take(MaxParameters).Select(p => ToValue(p, "instance")).ToList(),
                [],
                all.Count > MaxParameters);
        }

        var type = document.GetElement(element.GetTypeId()) as ElementType;
        var values = new List<ElementParameter>();
        var notFound = new List<string>();
        foreach (JsonElement nameElement in arguments.GetProperty("parameterNames").EnumerateArray())
        {
            string name = nameElement.GetString()!;
            if (element.LookupParameter(name) is { } instanceParameter)
            {
                values.Add(ToValue(instanceParameter, "instance"));
            }
            else if (type?.LookupParameter(name) is { } typeParameter)
            {
                values.Add(ToValue(typeParameter, "type"));
            }
            else
            {
                notFound.Add(name);
            }
        }

        return new ParametersResult(elementId, values, notFound, Truncated: false);
    }

    private static ElementParameter ToValue(Parameter parameter, string source)
    {
        double? valueMm = null;
        double? valueM2 = null;
        if (parameter.StorageType == StorageType.Double && parameter.HasValue)
        {
            ForgeTypeId spec = parameter.Definition.GetDataType();
            if (spec == SpecTypeId.Length)
            {
                valueMm = RevitRead.Mm(parameter.AsDouble());
            }
            else if (spec == SpecTypeId.Area)
            {
                valueM2 = RevitRead.M2(parameter.AsDouble());
            }
        }

        string? display = !parameter.HasValue ? null
            : parameter.StorageType == StorageType.String ? parameter.AsString()
            : parameter.AsValueString();

        return new ElementParameter(
            parameter.Definition.Name,
            source,
            parameter.StorageType.ToString(),
            display,
            valueMm,
            valueM2,
            parameter.IsReadOnly);
    }
}

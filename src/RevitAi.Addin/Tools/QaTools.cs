using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Standards;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Phase 5 model QA: read-only checks (docs/REVIT_TOOLS.md §34). None of these change the model.

internal static class Qa
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static int Limit(JsonElement arguments) =>
        (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);

    public static QaResult Result(string check, IReadOnlyList<QaElement> found, int limit, string? note = null) =>
        new(check, found.Count, found.Count > limit, found.Take(limit).ToList(), note);

    public const string LimitOnlySchema = """
        {
          "type": "object",
          "properties": {
            "limit": { "type": ["integer", "null"], "description": "Maximum results. Null for 50." }
          },
          "required": ["limit"],
          "additionalProperties": false
        }
        """;
}

public sealed class FindRoomsWithoutTagsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "find_rooms_without_tags";

    public override string Description =>
        "Finds placed rooms that have no room tag: in one view (viewId), or in no view at all (viewId null). Unplaced rooms are ignored.";

    public override string ProgressLabel => "Looking for untagged rooms…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewId": { "type": ["integer", "null"], "description": "Check only this view (see the context's viewId). Null: rooms not tagged in any view." },
            "limit": { "type": ["integer", "null"], "description": "Maximum results. Null for 50." }
          },
          "required": ["viewId", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        long? viewId = RevitRead.OptionalLong(arguments, "viewId");

        FilteredElementCollector rooms = viewId is null
            ? new FilteredElementCollector(document)
            : new FilteredElementCollector(document, new ElementId(viewId.Value));
        FilteredElementCollector tags = viewId is null
            ? new FilteredElementCollector(document)
            : new FilteredElementCollector(document, new ElementId(viewId.Value));

        var tagged = new HashSet<long>(tags.OfCategory(BuiltInCategory.OST_RoomTags).OfType<RoomTag>().Select(t => t.TaggedLocalRoomId.Value));
        List<QaElement> untagged = rooms
            .OfCategory(BuiltInCategory.OST_Rooms)
            .OfType<Room>()
            .Where(r => r.Area > 0 && !tagged.Contains(r.Id.Value))
            .Select(r => new QaElement(RevitRead.Summarize(r), null))
            .ToList();

        return Qa.Result(Name, untagged, Qa.Limit(arguments));
    }
}

public abstract class FindUnhostedTool(RevitDispatcher dispatcher, BuiltInCategory category, string what) : RevitReadTool(dispatcher)
{
    public override string Description => $"Finds {what} that are not hosted in a wall (orphaned after their host was deleted, or placed without one).";

    protected override string SchemaJson => Qa.LimitOnlySchema;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        List<QaElement> unhosted = new FilteredElementCollector(document)
            .OfCategory(category)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance => instance.Host is null)
            .Select(instance => new QaElement(RevitRead.Summarize(instance), "No host"))
            .ToList();
        return Qa.Result(Name, unhosted, Qa.Limit(arguments));
    }
}

public sealed class FindUnhostedDoorsTool(RevitDispatcher dispatcher) : FindUnhostedTool(dispatcher, BuiltInCategory.OST_Doors, "doors")
{
    public override string Name => "find_unhosted_doors";

    public override string ProgressLabel => "Looking for doors without a host…";
}

public sealed class FindUnhostedWindowsTool(RevitDispatcher dispatcher) : FindUnhostedTool(dispatcher, BuiltInCategory.OST_Windows, "windows")
{
    public override string Name => "find_unhosted_windows";

    public override string ProgressLabel => "Looking for windows without a host…";
}

public sealed class FindDuplicateElementsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "find_duplicate_elements";

    public override string Description =>
        "Finds groups of identical instances in the same place, as reported by Revit's own " +
        "'identical instances in the same place' warnings. Each group lists the duplicated elements.";

    public override string ProgressLabel => "Looking for duplicates…";

    protected override string SchemaJson => Qa.LimitOnlySchema;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        int limit = Qa.Limit(arguments);
        List<IReadOnlyList<ElementSummary>> groups = document.GetWarnings()
            .Where(w => w.GetFailureDefinitionId() == BuiltInFailures.OverlapFailures.DuplicateInstances)
            .Select(w => (IReadOnlyList<ElementSummary>)w.GetFailingElements()
                .Concat(w.GetAdditionalElements())
                .Distinct()
                .Select(document.GetElement)
                .Where(e => e is not null)
                .Select(RevitRead.Summarize)
                .ToList())
            .ToList();

        return new DuplicatesResult(groups.Count, groups.Count > limit, groups.Take(limit).ToList());
    }
}

public sealed class GetModelWarningsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int SampleIds = 10;

    public override string Name => "get_model_warnings";

    public override string Description =>
        "Returns the Revit warnings in the model (Manage → Warnings), grouped by message with a count and sample element IDs, " +
        "most frequent first. 'limit' caps the number of groups.";

    public override string ProgressLabel => "Reading the model's warnings…";

    protected override string SchemaJson => Qa.LimitOnlySchema;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        IList<FailureMessage> warnings = document.GetWarnings();
        int limit = Qa.Limit(arguments);

        List<WarningGroup> groups = warnings
            .GroupBy(w => w.GetDescriptionText())
            .Select(g => new WarningGroup(
                g.Key,
                g.Count(),
                g.SelectMany(w => w.GetFailingElements()).Select(id => id.Value).Distinct().Take(SampleIds).ToList()))
            .OrderByDescending(g => g.Count)
            .ToList();

        return new WarningsResult(warnings.Count, groups.Count, groups.Count > limit, groups.Take(limit).ToList());
    }
}

public sealed class FindNonstandardElementsTool(RevitDispatcher dispatcher, string standardsPath) : RevitReadTool(dispatcher)
{
    public override string Name => "find_nonstandard_elements";

    public override string Description =>
        "Finds elements of a category whose type is not in the project's standard types (standards.json). " +
        "Reports nothing useful when no standards are configured for the category.";

    public override string ProgressLabel => "Checking elements against the standards…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name as shown in Revit, e.g. Walls, Doors." },
            "limit": { "type": ["integer", "null"], "description": "Maximum results. Null for 50." }
          },
          "required": ["category", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        Category category = RevitRead.RequireCategory(document, arguments.GetProperty("category").GetString()!);
        IReadOnlyList<string> preferred = ProjectStandards.Load(standardsPath, out string? problem).PreferredFor(document.Title, category.Name);
        if (preferred.Count == 0)
        {
            return Qa.Result(Name, [], Qa.Limit(arguments),
                problem ?? $"No standard {category.Name} types are configured in standards.json, so nothing can be checked.");
        }

        List<QaElement> nonstandard = new FilteredElementCollector(document)
            .OfCategoryId(category.Id)
            .WhereElementIsNotElementType()
            .Where(e => document.GetElement(e.GetTypeId()) is ElementType type
                        && !preferred.Any(entry => ProjectStandards.Matches(entry, type.FamilyName, type.Name)))
            .Select(e => new QaElement(RevitRead.Summarize(e), "Type not in standards"))
            .ToList();

        return Qa.Result(Name, nonstandard, Qa.Limit(arguments));
    }
}

public sealed class FindElementsMissingParameterTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "find_elements_missing_parameter";

    public override string Description =>
        "Finds elements of a category where a parameter (instance first, then type) is missing or has no value, " +
        "e.g. doors without a Mark or rooms without a Number.";

    public override string ProgressLabel => "Looking for missing parameter values…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name as shown in Revit, e.g. Doors, Rooms." },
            "parameterName": { "type": "string", "description": "Parameter name as shown in the Properties palette, e.g. Mark, Fire Rating." },
            "limit": { "type": ["integer", "null"], "description": "Maximum results. Null for 50." }
          },
          "required": ["category", "parameterName", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        Category category = RevitRead.RequireCategory(document, arguments.GetProperty("category").GetString()!);
        string parameterName = arguments.GetProperty("parameterName").GetString()!;

        var found = new List<QaElement>();
        foreach (Element element in new FilteredElementCollector(document).OfCategoryId(category.Id).WhereElementIsNotElementType())
        {
            Parameter? parameter = element.LookupParameter(parameterName)
                ?? document.GetElement(element.GetTypeId())?.LookupParameter(parameterName);
            string? reason = parameter is null ? "Parameter missing"
                : !parameter.HasValue || (parameter.StorageType == StorageType.String && string.IsNullOrWhiteSpace(parameter.AsString())) ? "No value"
                : null;
            if (reason is not null)
            {
                found.Add(new QaElement(RevitRead.Summarize(element), reason));
            }
        }

        return Qa.Result(Name, found, Qa.Limit(arguments));
    }
}

public sealed class SelectElementsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int MaxElements = 500;

    public override string Name => "select_elements";

    public override string Description =>
        "Selects elements in Revit and zooms to them, so the user can see them (e.g. after a check). " +
        $"Changes only the selection, never the model. Up to {MaxElements} elements.";

    public override string ProgressLabel => "Selecting elements in Revit…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": "array", "items": { "type": "integer" }, "description": "Elements to select." }
          },
          "required": ["elementIds"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        UIDocument uiDocument = RevitRead.RequireUiDocument(app);
        Document document = uiDocument.Document;
        List<long> requested = arguments.GetProperty("elementIds").EnumerateArray().Select(id => id.GetInt64()).Distinct().Take(MaxElements).ToList();
        List<ElementId> existing = requested.Select(id => new ElementId(id)).Where(id => document.GetElement(id) is not null).ToList();

        uiDocument.Selection.SetElementIds(existing);
        if (existing.Count > 0)
        {
            try
            {
                uiDocument.ShowElements(existing);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Not visible in any open view; the selection still applies.
            }
        }

        return new SelectionChangeResult(existing.Count, requested.Except(existing.Select(id => id.Value)).ToList());
    }
}

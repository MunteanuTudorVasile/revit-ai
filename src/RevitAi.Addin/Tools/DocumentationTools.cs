using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Localization;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Phase 4 documentation tools (docs/REVIT_TOOLS.md). Write tools follow plan → preview → apply (ADR-024).

public sealed class FindViewsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public override string Name => "find_views";

    public override string Description =>
        "Lists views, sheets and schedules: ID, name, view type, level and the sheet number it is placed on. " +
        "With templatesOnly true, lists view templates instead. viewType uses Revit's names, e.g. FloorPlan, CeilingPlan, " +
        $"Section, Elevation, ThreeD, DrawingSheet, Schedule, Legend. Up to 'limit' results (default {DefaultLimit}, max {MaxLimit}).";

    public override string ProgressLabel => "Looking up views…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewType": { "type": ["string", "null"], "description": "Revit view type, e.g. FloorPlan, DrawingSheet, Schedule. Null for all." },
            "nameContains": { "type": ["string", "null"], "description": "Case-insensitive text the name must contain. Null for any." },
            "templatesOnly": { "type": "boolean", "description": "True to list view templates instead of views." },
            "limit": { "type": ["integer", "null"], "description": "Maximum results. Null for the default." }
          },
          "required": ["viewType", "nameContains", "templatesOnly", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        bool templatesOnly = arguments.GetProperty("templatesOnly").GetBoolean();
        string? nameContains = RevitRead.OptionalString(arguments, "nameContains");
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);

        ViewType? viewType = null;
        if (RevitRead.OptionalString(arguments, "viewType") is { } typeName)
        {
            viewType = Enum.TryParse(typeName, ignoreCase: true, out ViewType parsed)
                ? parsed
                : throw new ToolException($"Unknown view type '{typeName}'. Use e.g. FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DrawingSheet, Schedule.");
        }

        List<ViewInfo> views = new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => v.IsTemplate == templatesOnly && DocumentationViews.IsListable(v))
            .Where(v => viewType is null || v.ViewType == viewType)
            .Where(v => nameContains is null || v.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.ViewType.ToString())
            .ThenBy(v => v.Name)
            .Select(v => new ViewInfo(v.Id.Value, v.Name, v.ViewType.ToString(), v.GenLevel?.Name, DocumentationViews.SheetNumber(v), v.IsTemplate))
            .ToList();

        return new ViewListResult(views.Count, views.Count > limit, views.Take(limit).ToList());
    }
}

public sealed class CreateViewTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "create_view";

    public override string Description =>
        "Proposes a new floor plan or ceiling plan for a level, optionally named and with a view template (find_views templatesOnly).";

    public override string ProgressLabel => "Checking the view…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewType": { "type": "string", "enum": ["FloorPlan", "CeilingPlan"] },
            "levelId": { "type": "integer", "description": "Level ID." },
            "name": { "type": ["string", "null"], "description": "View name. Null for Revit's automatic name." },
            "viewTemplateId": { "type": ["integer", "null"], "description": "View template ID. Null for none." }
          },
          "required": ["viewType", "levelId", "name", "viewTemplateId"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        string kind = T[inputs.Family == ViewFamily.FloorPlan ? "Tool.FloorPlan" : "Tool.CeilingPlan"];
        string summary = inputs.Name is null
            ? T.Format("Tool.ViewSummaryUnnamed", kind, inputs.Level.Name)
            : T.Format("Tool.ViewSummary", kind, inputs.Name, inputs.Level.Name);
        return summary + (inputs.Template is null ? "" : T.Format("Tool.TemplateSuffix", inputs.Template.Name));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        ViewPlan view = ViewPlan.Create(document, inputs.ViewFamilyType.Id, inputs.Level.Id);
        if (inputs.Name is not null)
        {
            view.Name = inputs.Name;
        }

        if (inputs.Template is not null)
        {
            view.ViewTemplateId = inputs.Template.Id;
        }

        return new OperationResult(view.Id.Value, T.Format("Tool.ViewCreated", view.Id.Value, view.Name));
    }

    private Inputs Resolve(Document document, JsonElement arguments)
    {
        ViewFamily family = arguments.GetProperty("viewType").GetString() == "CeilingPlan" ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());

        ViewFamilyType viewFamilyType = new FilteredElementCollector(document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)
            ?? throw new ToolException(T.Format("Tool.NoViewFamilyType", T[family == ViewFamily.FloorPlan ? "Tool.FloorPlan" : "Tool.CeilingPlan"]));

        string? name = RevitRead.OptionalString(arguments, "name");
        ViewType viewType = family == ViewFamily.FloorPlan ? ViewType.FloorPlan : ViewType.CeilingPlan;
        if (name is not null && DocumentationViews.NameTaken(document, name, viewType))
        {
            throw new ToolException(T.Format("Tool.ViewNameTaken", name));
        }

        View? template = null;
        if (RevitRead.OptionalLong(arguments, "viewTemplateId") is { } templateId)
        {
            template = document.GetElement(new ElementId(templateId)) as View;
            if (template is not { IsTemplate: true })
            {
                throw new ToolException(T.Format("Tool.NotATemplate", templateId));
            }
        }

        return new Inputs(family, level, viewFamilyType, name, template);
    }

    private sealed record Inputs(ViewFamily Family, Level Level, ViewFamilyType ViewFamilyType, string? Name, View? Template);
}

public sealed class CreateSheetTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "create_sheet";

    public override string Description =>
        "Proposes a new sheet with a number, name and title block, and places the given views and schedules on it side by side " +
        "(the user can rearrange them). Title block types: find_family_types with category 'Title Blocks'; null for the default. " +
        "viewIds may include $opN.elementId for views or schedules created earlier in this plan.";

    public override string ProgressLabel => "Checking the sheet…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "string", "description": "Sheet number, e.g. A101. Must be unique." },
            "name": { "type": "string", "description": "Sheet name." },
            "titleBlockTypeId": { "type": ["integer", "null"], "description": "Title block type ID. Null for the project default." },
            "viewIds": {
              "type": "array",
              "items": { "type": ["integer", "string"] },
              "description": "Views or schedules to place, or $opN.elementId references. Empty for none."
            }
          },
          "required": ["number", "name", "titleBlockTypeId", "viewIds"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        (string number, string name, FamilySymbol titleBlock) = Resolve(document, arguments);
        List<JsonElement> viewIds = arguments.GetProperty("viewIds").EnumerateArray().ToList();
        foreach (JsonElement id in viewIds.Where(v => v.ValueKind == JsonValueKind.Number))
        {
            PlaceableView(document, id.GetInt64());
        }

        return T.Format("Tool.SheetSummary", number, name, titleBlock.Name)
               + (viewIds.Count == 0 ? "" : T.Format("Tool.SheetViewsSuffix", viewIds.Count));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        (string number, string name, FamilySymbol titleBlock) = Resolve(document, arguments);
        List<View> views = arguments.GetProperty("viewIds").EnumerateArray()
            .Select(id => PlaceableView(document, id.GetInt64()))
            .ToList();

        ViewSheet sheet = ViewSheet.Create(document, titleBlock.Id);
        sheet.SheetNumber = number;
        sheet.Name = name;
        document.Regenerate();

        // Simple side-by-side layout across the sheet; the user arranges the final layout.
        BoundingBoxUV outline = sheet.Outline;
        var placed = new List<long>();
        for (int i = 0; i < views.Count; i++)
        {
            double u = outline.Min.U + ((i + 0.5) * (outline.Max.U - outline.Min.U) / views.Count);
            var point = new XYZ(u, (outline.Min.V + outline.Max.V) / 2, 0);
            placed.Add(Place(document, sheet, views[i], point));
        }

        return new OperationResult(sheet.Id.Value, T.Format("Tool.SheetCreated", number, name, sheet.Id.Value, views.Count), placed);
    }

    private long Place(Document document, ViewSheet sheet, View view, XYZ point)
    {
        if (view is ViewSchedule schedule)
        {
            return ScheduleSheetInstance.Create(document, sheet.Id, schedule.Id, point).Id.Value;
        }

        return Viewport.CanAddViewToSheet(document, sheet.Id, view.Id)
            ? Viewport.Create(document, sheet.Id, view.Id, point).Id.Value
            : throw new ToolException(T.Format("Tool.CannotPlaceView", view.Name));
    }

    private (string Number, string Name, FamilySymbol TitleBlock) Resolve(Document document, JsonElement arguments)
    {
        string number = arguments.GetProperty("number").GetString()!.Trim();
        if (number.Length == 0)
        {
            throw new ToolException(T["Tool.SheetNumberEmpty"]);
        }

        bool taken = new FilteredElementCollector(document)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Any(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
        if (taken)
        {
            throw new ToolException(T.Format("Tool.SheetNumberTaken", number));
        }

        FamilySymbol titleBlock = WriteArgs.FamilyType(
            T, document, RevitRead.OptionalLong(arguments, "titleBlockTypeId"), BuiltInCategory.OST_TitleBlocks, "Tool.CatTitleBlocks");
        return (number, arguments.GetProperty("name").GetString()!, titleBlock);
    }

    private View PlaceableView(Document document, long id)
    {
        if (document.GetElement(new ElementId(id)) is not View view || view.IsTemplate || view is ViewSheet || !DocumentationViews.IsListable(view))
        {
            throw new ToolException(T.Format("Tool.NotAPlaceableView", id));
        }

        // Schedules may appear on several sheets; other views only on one.
        if (view is not ViewSchedule && DocumentationViews.SheetNumber(view) is { } sheetNumber)
        {
            throw new ToolException(T.Format("Tool.ViewOnSheet", view.Name, sheetNumber));
        }

        return view;
    }
}

public sealed class CreateScheduleTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int MaxFieldsListed = 30;

    public override string Name => "create_schedule";

    public override string Description =>
        "Proposes a schedule of one category with the given fields (column names as shown in Revit, e.g. Mark, Family and Type, Width, Level). " +
        "Unknown field names make the preview fail with a list of available fields.";

    public override string ProgressLabel => "Checking the schedule…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name as shown in Revit, e.g. Doors, Windows, Rooms." },
            "name": { "type": "string", "description": "Schedule name. Must be unique." },
            "fields": { "type": "array", "items": { "type": "string" }, "description": "Field names in column order." }
          },
          "required": ["category", "name", "fields"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        (Category category, string name, List<string> fields) = Resolve(document, arguments);
        return T.Format("Tool.ScheduleSummary", name, category.Name, string.Join(", ", fields));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        (Category category, string name, List<string> fields) = Resolve(document, arguments);
        ViewSchedule schedule = ViewSchedule.CreateSchedule(document, category.Id);
        schedule.Name = name;

        ScheduleDefinition definition = schedule.Definition;
        IList<SchedulableField> available = definition.GetSchedulableFields();
        foreach (string field in fields)
        {
            SchedulableField match = available.FirstOrDefault(f => string.Equals(f.GetName(document), field, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException(T.Format("Tool.ScheduleUnknownField", field, category.Name,
                    string.Join(", ", available.Select(f => f.GetName(document)).Distinct().Take(MaxFieldsListed))));
            definition.AddField(match);
        }

        return new OperationResult(schedule.Id.Value, T.Format("Tool.ScheduleCreated", schedule.Id.Value, name, fields.Count));
    }

    private (Category Category, string Name, List<string> Fields) Resolve(Document document, JsonElement arguments)
    {
        Category category = RevitRead.RequireCategory(document, arguments.GetProperty("category").GetString()!);
        if (!ViewSchedule.IsValidCategoryForSchedule(category.Id))
        {
            throw new ToolException(T.Format("Tool.ScheduleCategory", category.Name));
        }

        string name = arguments.GetProperty("name").GetString()!.Trim();
        if (DocumentationViews.NameTaken(document, name, ViewType.Schedule))
        {
            throw new ToolException(T.Format("Tool.ScheduleNameTaken", name));
        }

        List<string> fields = arguments.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!.Trim()).Where(f => f.Length > 0).ToList();
        return fields.Count == 0 ? throw new ToolException(T["Tool.ScheduleNoFields"]) : (category, name, fields);
    }
}

public sealed class TagElementsTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "tag_elements";

    public override string Description =>
        "Proposes tags in a plan, section or elevation using each category's default tag type. Either tag every untagged element of a " +
        "category visible in the view (category, elementIds null), or tag specific elements (elementIds, category null). " +
        "Rooms get room tags. Elements that already have a tag in the view are skipped.";

    public override string ProgressLabel => "Checking the tags…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewId": { "type": ["integer", "string"], "description": "View ID (see the context's viewId), or $opN.elementId." },
            "category": { "type": ["string", "null"], "description": "Tag all untagged elements of this category in the view, e.g. Rooms, Doors. Null when elementIds is given." },
            "elementIds": { "type": ["array", "null"], "items": { "type": ["integer", "string"] }, "description": "Specific elements to tag. Null when category is given." }
          },
          "required": ["viewId", "category", "elementIds"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        string? categoryName = RevitRead.OptionalString(arguments, "category");
        bool hasIds = arguments.GetProperty("elementIds").ValueKind == JsonValueKind.Array;
        if ((categoryName is null) == !hasIds)
        {
            throw new ToolException(T["Tool.TagNeedsTarget"]);
        }

        long? viewId = WriteArgs.IdOrReference(T, arguments, "viewId");
        string viewText = viewId is null ? arguments.GetProperty("viewId").GetString()! : TaggableView(document, viewId.Value).Name;
        if (hasIds)
        {
            return T.Format("Tool.TagSummaryIds", arguments.GetProperty("elementIds").GetArrayLength(), viewText);
        }

        if (viewId is null)
        {
            return T.Format("Tool.TagSummaryRef", categoryName!, viewText);
        }

        View view = TaggableView(document, viewId.Value);

        Category category = RevitRead.RequireCategory(document, categoryName!);
        int count = UntaggedInView(document, view, category).Count;
        return count == 0
            ? throw new ToolException(T.Format("Tool.NothingToTag", view.Name))
            : T.Format("Tool.TagSummary", count, category.Name, view.Name);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        View view = TaggableView(document, WriteArgs.Id(T, arguments, "viewId"));
        List<Element> targets = RevitRead.OptionalString(arguments, "category") is { } categoryName
            ? UntaggedInView(document, view, RevitRead.RequireCategory(document, categoryName))
            : arguments.GetProperty("elementIds").EnumerateArray()
                .Select(id => RevitRead.RequireElement(document, id.GetInt64()))
                .ToList();

        var tags = new List<long>();
        foreach (Element element in targets)
        {
            if (TagPoint(element) is not { } point)
            {
                continue;
            }

            Element tag = element is Room room
                ? document.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id)
                : IndependentTag.Create(document, view.Id, new Reference(element), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
            tags.Add(tag.Id.Value);
        }

        if (tags.Count == 0)
        {
            throw new ToolException(T.Format("Tool.NothingToTag", view.Name));
        }

        return new OperationResult(null, T.Format("Tool.TagsCreated", tags.Count, view.Name), tags);
    }

    private View TaggableView(Document document, long id)
    {
        View view = document.GetElement(new ElementId(id)) as View ?? throw new ToolException(T.Format("Tool.NotAView", id));
        return view.IsTemplate || view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Section
            or ViewType.Elevation or ViewType.AreaPlan or ViewType.EngineeringPlan or ViewType.Detail)
            ? throw new ToolException(T.Format("Tool.ViewNotTaggable", view.Name, view.ViewType))
            : view;
    }

    private static List<Element> UntaggedInView(Document document, View view, Category category)
    {
        List<Element> elements = new FilteredElementCollector(document, view.Id)
            .OfCategoryId(category.Id)
            .WhereElementIsNotElementType()
            .ToList();

        var tagged = new HashSet<long>(new FilteredElementCollector(document, view.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>()
            .SelectMany(t => t.GetTaggedLocalElementIds())
            .Select(id => id.Value));
        tagged.UnionWith(new FilteredElementCollector(document, view.Id)
            .OfCategory(BuiltInCategory.OST_RoomTags)
            .OfType<RoomTag>()
            .Select(t => t.TaggedLocalRoomId.Value));

        // Unplaced or unenclosed rooms cannot be tagged.
        return elements.Where(e => !tagged.Contains(e.Id.Value) && e is not Room { Area: <= 0 }).ToList();
    }

    private static XYZ? TagPoint(Element element) => element.Location switch
    {
        LocationPoint point => point.Point,
        LocationCurve curve => curve.Curve.Evaluate(0.5, true),
        _ => element.get_BoundingBox(null) is { } box ? (box.Min + box.Max) / 2 : null,
    };
}

public sealed class CreateTextTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "create_text";

    public override string Description =>
        "Proposes a text note in a view using the project's default text type. Position in mm: model coordinates in plans, " +
        "sections and elevations; sheet coordinates (from the sheet's origin) on sheets.";

    public override string ProgressLabel => "Checking the text…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewId": { "type": ["integer", "string"], "description": "View or sheet ID, or $opN.elementId." },
            "position": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false },
            "text": { "type": "string" }
          },
          "required": ["viewId", "position", "text"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        string note = NoteText(arguments);
        TextTypeId(document);
        var position = WriteArgs.Point(arguments, "position");
        long? viewId = WriteArgs.IdOrReference(T, arguments, "viewId");
        string viewName = viewId is null ? arguments.GetProperty("viewId").GetString()! : AnnotationView(document, viewId.Value).Name;
        return T.Format("Tool.TextSummary", note, viewName, position.X, position.Y);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        View view = AnnotationView(document, WriteArgs.Id(T, arguments, "viewId"));
        var position = WriteArgs.Point(arguments, "position");
        TextNote note = TextNote.Create(document, view.Id, WriteArgs.ToXyz(position), NoteText(arguments), TextTypeId(document));
        return new OperationResult(note.Id.Value, T.Format("Tool.TextCreated", note.Id.Value, view.Name));
    }

    private string NoteText(JsonElement arguments)
    {
        string note = arguments.GetProperty("text").GetString()!.Trim();
        return note.Length == 0 ? throw new ToolException(T["Tool.TextEmpty"]) : note;
    }

    private ElementId TextTypeId(Document document)
    {
        ElementId id = document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        return id == ElementId.InvalidElementId ? throw new ToolException(T["Tool.NoTextType"]) : id;
    }

    private View AnnotationView(Document document, long id)
    {
        View view = document.GetElement(new ElementId(id)) as View ?? throw new ToolException(T.Format("Tool.NotAView", id));
        return view.IsTemplate || view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.ProjectBrowser
            or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined
            ? throw new ToolException(T.Format("Tool.ViewNoAnnotation", view.Name, view.ViewType))
            : view;
    }
}

public sealed class DimensionWallTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double DefaultOffsetMm = 1000;

    public override string Name => "dimension_wall";

    public override string Description =>
        "Proposes an overall length dimension for a straight wall in a plan view, placed parallel to the wall at offsetMm " +
        "(default 1000 mm; negative for the other side). Works best for walls with free ends.";

    public override string ProgressLabel => "Checking the dimension…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewId": { "type": ["integer", "string"], "description": "Plan view ID (see the context's viewId), or $opN.elementId." },
            "wallId": { "type": ["integer", "string"], "description": "Wall ID, or $opN.elementId." },
            "offsetMm": { "type": ["number", "null"], "description": "Distance of the dimension line from the wall's location line. Null for 1000 mm." }
          },
          "required": ["viewId", "wallId", "offsetMm"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        double offset = WriteArgs.OptionalNumber(arguments, "offsetMm") ?? DefaultOffsetMm;
        long? viewId = WriteArgs.IdOrReference(T, arguments, "viewId");
        long? wallId = WriteArgs.IdOrReference(T, arguments, "wallId");
        string viewName = viewId is null ? arguments.GetProperty("viewId").GetString()! : PlanView(document, viewId.Value).Name;

        string wallText = wallId is null ? arguments.GetProperty("wallId").GetString()! : T.Format("Tool.WallRef", wallId);
        string length = wallId is null
            ? "?"
            : WriteArgs.Mm(UnitUtils.ConvertFromInternalUnits(WriteArgs.WallLine(T, WriteArgs.Wall(T, document, wallId.Value)).Length, UnitTypeId.Millimeters));
        return T.Format("Tool.DimSummary", wallText, length, viewName, WriteArgs.Mm(offset));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        View view = PlanView(document, WriteArgs.Id(T, arguments, "viewId"));
        Wall wall = WriteArgs.Wall(T, document, WriteArgs.Id(T, arguments, "wallId"));
        Line line = WriteArgs.WallLine(T, wall);
        XYZ direction = line.Direction;

        // The wall's end faces are the planar faces whose normal is parallel to the wall.
        var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
        List<PlanarFace> ends = (wall.get_Geometry(options) ?? throw new ToolException(T.Format("Tool.DimNoEnds", wall.Id.Value)))
            .OfType<Solid>()
            .SelectMany(solid => solid.Faces.OfType<PlanarFace>())
            .Where(face => face.Reference is not null && Math.Abs(Math.Abs(face.FaceNormal.DotProduct(direction)) - 1) < 1e-6)
            .OrderBy(face => face.Origin.DotProduct(direction))
            .ToList();
        if (ends.Count < 2)
        {
            throw new ToolException(T.Format("Tool.DimNoEnds", wall.Id.Value));
        }

        var references = new ReferenceArray();
        references.Append(ends[0].Reference);
        references.Append(ends[^1].Reference);

        XYZ offset = XYZ.BasisZ.CrossProduct(direction).Normalize() * RevitRead.Feet(WriteArgs.OptionalNumber(arguments, "offsetMm") ?? DefaultOffsetMm);
        Line dimensionLine = Line.CreateBound(line.GetEndPoint(0) + offset, line.GetEndPoint(1) + offset);
        Dimension dimension = document.Create.NewDimension(view, dimensionLine, references);

        return new OperationResult(dimension.Id.Value, T.Format("Tool.DimCreated", dimension.Id.Value, dimension.ValueString, wall.Id.Value));
    }

    private View PlanView(Document document, long id)
    {
        View view = document.GetElement(new ElementId(id)) as View ?? throw new ToolException(T.Format("Tool.NotAView", id));
        return view is ViewPlan { IsTemplate: false } ? view : throw new ToolException(T.Format("Tool.ViewNotPlan", view.Name));
    }
}

/// <summary>View helpers shared by the documentation tools.</summary>
internal static class DocumentationViews
{
    public static bool IsListable(View view) =>
        view.ViewType is not (ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined);

    /// <summary>The number of the sheet the view is placed on, or null (sheets return their own number).</summary>
    public static string? SheetNumber(View view) =>
        view is ViewSheet sheet
            ? sheet.SheetNumber
            : RevitRead.NullIfEmpty(view.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString());

    public static bool NameTaken(Document document, string name, ViewType viewType) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Any(v => !v.IsTemplate && v.ViewType == viewType && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Localization;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Grids from points and placing elements at points. Analysis is Revit-free (GridDetection); writes go through plans.

public sealed class FindGridLinesTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const double DefaultToleranceMm = 100;
    private const int MaxPoints = 5000;

    public override string Name => "find_grid_lines";

    public override string Description =>
        "Finds rows and columns in the positions of point-based elements (dots, columns, generic models…) and suggests " +
        "grid lines: vertical lines (constant X) and horizontal lines (constant Y), with spacings, elements that are not on " +
        "both a row and a column, and the names of existing grids. suggestedGrids are ready for create_grids: vertical lines " +
        "numbered 1, 2, 3…, horizontal lines lettered A, B, C… (no I/O), unused names, 1000 mm past the outermost points. " +
        "Source: elementIds, or a category, or (both null) the current selection. Axis-aligned to model X/Y.";

    public override string ProgressLabel => "Looking for rows and columns of points…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": ["array", "null"], "items": { "type": "integer" }, "description": "Elements to analyse. Null to use category or the selection." },
            "category": { "type": ["string", "null"], "description": "All elements of this category (optionally on levelId). Null to use elementIds or the selection." },
            "levelId": { "type": ["integer", "null"], "description": "With category: only elements on this level." },
            "toleranceMm": { "type": ["number", "null"], "description": "How far a point may be from a line and still belong to it. Null for 100 mm." },
            "minPointsPerLine": { "type": ["integer", "null"], "description": "Minimum points for a line. Null for 2." }
          },
          "required": ["elementIds", "category", "levelId", "toleranceMm", "minPointsPerLine"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        UIDocument uiDocument = RevitRead.RequireUiDocument(app);
        Document document = uiDocument.Document;
        List<Element> elements = Source(uiDocument, arguments);

        var located = elements
            .Select(e => (Element: e, Point: (e.Location as LocationPoint)?.Point))
            .Where(x => x.Point is not null)
            .Take(MaxPoints)
            .ToList();
        List<Point2> points = located.Select(x => new Point2(RevitRead.Mm(x.Point!.X), RevitRead.Mm(x.Point!.Y))).ToList();

        double tolerance = Math.Clamp(WriteArgs.OptionalNumber(arguments, "toleranceMm") ?? DefaultToleranceMm, 1, 10_000);
        int minPoints = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "minPointsPerLine") ?? 2, 2, 1000);
        GridDetectionResult result = GridDetection.Detect(points, tolerance, minPoints);
        List<string> existingNames = new FilteredElementCollector(document).OfClass(typeof(Grid)).Select(g => g.Name).OrderBy(n => n).ToList();

        return new GridLinesResult(
            points.Count,
            elements.Count - located.Count,
            result.VerticalLines,
            result.HorizontalLines,
            result.VerticalSpacingsMm,
            result.HorizontalSpacingsMm,
            result.OffGridPointIndexes.Select(i => located[i].Element.Id.Value).ToList(),
            existingNames,
            GridSuggestion.Suggest(result, existingNames));
    }

    private static List<Element> Source(UIDocument uiDocument, JsonElement arguments)
    {
        Document document = uiDocument.Document;
        if (arguments.GetProperty("elementIds").ValueKind == JsonValueKind.Array)
        {
            return arguments.GetProperty("elementIds").EnumerateArray().Select(id => RevitRead.RequireElement(document, id.GetInt64())).ToList();
        }

        if (RevitRead.OptionalString(arguments, "category") is { } categoryName)
        {
            long? levelId = RevitRead.OptionalLong(arguments, "levelId");
            return new FilteredElementCollector(document)
                .OfCategoryId(RevitRead.RequireCategory(document, categoryName).Id)
                .WhereElementIsNotElementType()
                .Where(e => levelId is null || e.LevelId.Value == levelId)
                .ToList();
        }

        return uiDocument.Selection.GetElementIds().Select(document.GetElement).Where(e => e is not null).ToList();
    }
}

public sealed class CreateGridsTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int MaxGrids = 100;
    private const double MinLengthMm = 100;
    private const int MaxListed = 8;

    public override string Name => "create_grids";

    public override string Description =>
        "Proposes straight grid lines, each with a unique name and two end points (model coordinates, mm). Names must not " +
        "exist yet (find_grid_lines lists existing ones). Usual naming: letters A, B, C… in one direction and numbers 1, 2, 3… " +
        $"in the other, unless the user or the standards say otherwise. At most {MaxGrids} at once.";

    public override string ProgressLabel => "Checking the grids…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "grids": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "start": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false },
                  "end": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false }
                },
                "required": ["name", "start", "end"],
                "additionalProperties": false
              }
            }
          },
          "required": ["grids"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        List<GridInput> grids = Inputs(document, arguments);
        return T.Format("Tool.GridsSummary", grids.Count, Names(grids));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        List<GridInput> grids = Inputs(document, arguments);
        var created = new List<long>();
        foreach (GridInput input in grids)
        {
            Grid grid = Grid.Create(document, Line.CreateBound(WriteArgs.ToXyz(input.Start), WriteArgs.ToXyz(input.End)));
            grid.Name = input.Name;
            created.Add(grid.Id.Value);
        }

        return new OperationResult(created[0], T.Format("Tool.GridsDone", created.Count, Names(grids)), created.Skip(1).ToList());
    }

    private List<GridInput> Inputs(Document document, JsonElement arguments)
    {
        List<GridInput> grids = arguments.GetProperty("grids").EnumerateArray()
            .Select(g => new GridInput(g.GetProperty("name").GetString()!.Trim(), WriteArgs.Point(g, "start"), WriteArgs.Point(g, "end")))
            .ToList();
        if (grids.Count == 0)
        {
            throw new ToolException(T["Tool.MoveNeedsElements"]);
        }

        if (grids.Count > MaxGrids)
        {
            throw new ToolException(T.Format("Tool.GridsTooMany", MaxGrids));
        }

        var existing = new FilteredElementCollector(document).OfClass(typeof(Grid)).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GridInput grid in grids)
        {
            if (grid.Name.Length == 0)
            {
                throw new ToolException(T["Tool.GridNameEmpty"]);
            }

            if (existing.Contains(grid.Name))
            {
                throw new ToolException(T.Format("Tool.GridNameTaken", grid.Name));
            }

            if (!seen.Add(grid.Name))
            {
                throw new ToolException(T.Format("Tool.GridNameRepeated", grid.Name));
            }

            if (Polygon2D.Distance(grid.Start, grid.End) < MinLengthMm)
            {
                throw new ToolException(T.Format("Tool.GridTooShort", grid.Name, WriteArgs.Mm(MinLengthMm)));
            }
        }

        return grids;
    }

    private static string Names(List<GridInput> grids) =>
        string.Join(", ", grids.Take(MaxListed).Select(g => g.Name)) + (grids.Count > MaxListed ? ", …" : "");

    private sealed record GridInput(string Name, Point2 Start, Point2 End);
}

public sealed class PlaceFamilyInstancesTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int MaxPoints = 500;

    public override string Name => "place_family_instances";

    public override string Description =>
        "Proposes placing one level-based family type (columns, generic models, furniture, equipment…) at a list of points " +
        "on a level, optionally rotated. Wall- or face-hosted families (doors, windows) are refused: use create_door / create_window. " +
        $"Find types with find_family_types. At most {MaxPoints} points.";

    public override string ProgressLabel => "Checking the placement…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "familyTypeId": { "type": "integer", "description": "Family type ID (find_family_types)." },
            "levelId": { "type": "integer", "description": "Level ID." },
            "points": {
              "type": "array",
              "items": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false },
              "description": "Insertion points, model coordinates in mm."
            },
            "rotationDegrees": { "type": ["number", "null"], "description": "Rotation about the vertical axis. Null for 0." }
          },
          "required": ["familyTypeId", "levelId", "points", "rotationDegrees"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        string rotation = inputs.RotationDegrees == 0 ? "" : T.Format("Tool.PlaceRotation", inputs.RotationDegrees);
        return T.Format("Tool.PlaceSummary", inputs.Points.Count, $"{inputs.Type.FamilyName}: {inputs.Type.Name}", inputs.Level.Name, rotation);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        if (!inputs.Type.IsActive)
        {
            inputs.Type.Activate();
        }

        StructuralType structural = inputs.Type.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns
            ? StructuralType.Column
            : StructuralType.NonStructural;

        var placed = new List<long>();
        foreach (Point2 point in inputs.Points)
        {
            // Z is an offset from the level for level-based placement (verified in Revit 2026 by the self-test), so 0 = on the level.
            var location = new XYZ(RevitRead.Feet(point.X), RevitRead.Feet(point.Y), 0);
            FamilyInstance instance = document.Create.NewFamilyInstance(location, inputs.Type, inputs.Level, structural);
            if (inputs.RotationDegrees != 0)
            {
                ElementTransformUtils.RotateElement(document, instance.Id, Line.CreateUnbound(location, XYZ.BasisZ),
                    UnitUtils.ConvertToInternalUnits(inputs.RotationDegrees, UnitTypeId.Degrees));
            }

            placed.Add(instance.Id.Value);
        }

        return new OperationResult(placed[0], T.Format("Tool.PlaceDone", placed.Count, $"{inputs.Type.FamilyName}: {inputs.Type.Name}", inputs.Level.Name),
            placed.Skip(1).ToList());
    }

    private Inputs Resolve(Document document, JsonElement arguments)
    {
        long typeId = arguments.GetProperty("familyTypeId").GetInt64();
        FamilySymbol type = document.GetElement(new ElementId(typeId)) as FamilySymbol
            ?? throw new ToolException(T.Format("Tool.NotAFamilyType", typeId));
        if (type.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
        {
            throw new ToolException(T.Format("Tool.PlaceNotLevelBased", $"{type.FamilyName}: {type.Name}"));
        }

        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());
        List<Point2> points = arguments.GetProperty("points").EnumerateArray()
            .Select(p => new Point2(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble()))
            .ToList();

        return points.Count == 0 ? throw new ToolException(T["Tool.PlaceNeedsPoints"])
            : points.Count > MaxPoints ? throw new ToolException(T.Format("Tool.PlaceTooMany", MaxPoints))
            : new Inputs(type, level, points, WriteArgs.OptionalNumber(arguments, "rotationDegrees") ?? 0);
    }

    private sealed record Inputs(FamilySymbol Type, Level Level, IReadOnlyList<Point2> Points, double RotationDegrees);
}

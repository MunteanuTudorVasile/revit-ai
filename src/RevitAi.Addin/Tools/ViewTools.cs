using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Localization;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Sections, elevations, 3D views and room dimensions (Phase 4 completion). Plan → preview → apply (ADR-024).

public sealed class CreateSectionTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MinLengthMm = 100;
    private const double MaxSizeMm = 200_000;
    private const double DefaultDepthMm = 5000;
    private const double DefaultHeightMm = 4000;
    private const double BelowLevelMm = 500;

    public override string Name => "create_section";

    public override string Description =>
        "Proposes a section view along a line in plan from start to end (model coordinates, mm). The section looks to the LEFT of the " +
        "start→end direction; swap start and end to look the other way. It shows depthMm beyond the line (default 5000) and " +
        "heightMm above the level (default 4000), starting 500 mm below the level.";

    public override string ProgressLabel => "Checking the section…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "start": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false },
            "end": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false },
            "levelId": { "type": "integer", "description": "Level the section height is measured from." },
            "depthMm": { "type": ["number", "null"], "description": "How far beyond the line the section shows. Null for 5000 mm." },
            "heightMm": { "type": ["number", "null"], "description": "Height above the level. Null for 4000 mm." },
            "name": { "type": ["string", "null"], "description": "View name. Null for Revit's automatic name." }
          },
          "required": ["start", "end", "levelId", "depthMm", "heightMm", "name"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        return T.Format("Tool.SectionSummary", NameSuffix(inputs.Name), inputs.Start.X, inputs.Start.Y, inputs.End.X, inputs.End.Y,
            WriteArgs.Mm(inputs.DepthMm), WriteArgs.Mm(inputs.HeightMm), inputs.Level.Name);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        XYZ start = WriteArgs.ToXyz(inputs.Start);
        XYZ end = WriteArgs.ToXyz(inputs.End);
        XYZ direction = (end - start).Normalize();
        double halfLength = start.DistanceTo(end) / 2;
        double bottom = inputs.Level.ProjectElevation - RevitRead.Feet(BelowLevelMm);
        double top = inputs.Level.ProjectElevation + RevitRead.Feet(inputs.HeightMm);

        // Local frame: X along the line, Y up, Z = view direction (towards the viewer). The cut plane is at Z = 0 and the
        // view looks towards -Z, i.e. to the left of start→end, for depth along -Z.
        var transform = Transform.Identity;
        transform.Origin = (start + end) / 2;
        transform.BasisX = direction;
        transform.BasisY = XYZ.BasisZ;
        transform.BasisZ = direction.CrossProduct(XYZ.BasisZ);

        var box = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = new XYZ(-halfLength, bottom, -RevitRead.Feet(inputs.DepthMm)),
            Max = new XYZ(halfLength, top, 0),
        };

        ViewSection section = ViewSection.CreateSection(document, ViewTools.ViewFamilyTypeId(T, document, ViewFamily.Section, "Section"), box);
        if (inputs.Name is not null)
        {
            section.Name = inputs.Name;
        }

        return new OperationResult(section.Id.Value, T.Format("Tool.SectionCreated", section.Id.Value, section.Name));
    }

    private string NameSuffix(string? name) => name is null ? "" : T.Format("Tool.NamedSuffix", name);

    private Inputs Resolve(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());
        Point2 start = WriteArgs.Point(arguments, "start");
        Point2 end = WriteArgs.Point(arguments, "end");
        if (Polygon2D.Distance(start, end) < MinLengthMm)
        {
            throw new ToolException(T.Format("Tool.SectionTooShort", WriteArgs.Mm(MinLengthMm)));
        }

        double depth = WriteArgs.OptionalNumber(arguments, "depthMm") ?? DefaultDepthMm;
        double height = WriteArgs.OptionalNumber(arguments, "heightMm") ?? DefaultHeightMm;
        if (depth <= 0 || height <= 0 || depth > MaxSizeMm || height > MaxSizeMm)
        {
            throw new ToolException(T.Format("Tool.SectionRange", WriteArgs.Mm(MaxSizeMm)));
        }

        string? name = RevitRead.OptionalString(arguments, "name");
        if (name is not null && DocumentationViews.NameTaken(document, name, ViewType.Section))
        {
            throw new ToolException(T.Format("Tool.ViewNameTaken", name));
        }

        return new Inputs(level, start, end, depth, height, name);
    }

    private sealed record Inputs(Level Level, Point2 Start, Point2 End, double DepthMm, double HeightMm, string? Name);
}

public sealed class CreateElevationsTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int DefaultScale = 100;

    private static readonly Dictionary<string, XYZ> Directions = new()
    {
        ["north"] = XYZ.BasisY,
        ["east"] = XYZ.BasisX,
        ["south"] = -XYZ.BasisY,
        ["west"] = -XYZ.BasisX,
    };

    public override string Name => "create_elevations";

    public override string Description =>
        "Proposes elevation views from one marker placed at a point in a plan view, looking north, east, south and/or west " +
        "(project directions: north = +Y). Returns the first elevation as the operation's element.";

    public override string ProgressLabel => "Checking the elevations…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "planViewId": { "type": ["integer", "string"], "description": "Plan view to place the marker in (see the context's viewId), or $opN.elementId." },
            "point": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false, "description": "Marker position, model coordinates in mm." },
            "directions": { "type": "array", "items": { "type": "string", "enum": ["north", "east", "south", "west"] }, "description": "Directions the elevations look towards." }
          },
          "required": ["planViewId", "point", "directions"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        List<string> directions = RequestedDirections(arguments);
        Point2 point = WriteArgs.Point(arguments, "point");
        long? viewId = WriteArgs.IdOrReference(T, arguments, "planViewId");
        string viewName = viewId is null ? arguments.GetProperty("planViewId").GetString()! : ViewTools.PlanView(T, document, viewId.Value).Name;
        ViewTools.ViewFamilyTypeId(T, document, ViewFamily.Elevation, "Elevation");
        return T.Format("Tool.ElevationSummary", string.Join(", ", directions.Select(DirectionText)), point.X, point.Y, viewName);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        List<string> directions = RequestedDirections(arguments);
        View plan = ViewTools.PlanView(T, document, WriteArgs.Id(T, arguments, "planViewId"));
        ElevationMarker marker = ElevationMarker.CreateElevationMarker(
            document, ViewTools.ViewFamilyTypeId(T, document, ViewFamily.Elevation, "Elevation"), WriteArgs.ToXyz(WriteArgs.Point(arguments, "point")), DefaultScale);

        var created = new List<ViewSection>();
        foreach (string direction in directions)
        {
            created.Add(CreateLooking(document, marker, plan, direction));
        }

        return new OperationResult(
            created[0].Id.Value,
            T.Format("Tool.ElevationCreated", string.Join(", ", created.Select(v => $"{v.Id.Value} '{v.Name}'"))),
            [marker.Id.Value, .. created.Skip(1).Select(v => v.Id.Value)]);
    }

    /// <summary>
    /// The marker's slot order is not documented, so each free slot is tried and kept only when the new view looks the requested way.
    /// A view's ViewDirection points towards the viewer, so it looks along -ViewDirection.
    /// </summary>
    private ViewSection CreateLooking(Document document, ElevationMarker marker, View plan, string direction)
    {
        for (int index = 0; index < 4; index++)
        {
            if (!marker.IsAvailableIndex(index))
            {
                continue;
            }

            ViewSection view = marker.CreateElevation(document, plan.Id, index);
            if ((-view.ViewDirection).DotProduct(Directions[direction]) > 0.99)
            {
                return view;
            }

            document.Delete(view.Id);
        }

        throw new ToolException(T.Format("Tool.ElevationNoSlot", DirectionText(direction)));
    }

    private List<string> RequestedDirections(JsonElement arguments)
    {
        List<string> directions = arguments.GetProperty("directions").EnumerateArray().Select(d => d.GetString()!).Distinct().ToList();
        return directions.Count == 0 ? throw new ToolException(T["Tool.ElevationNeedsDirection"]) : directions;
    }

    private string DirectionText(string direction) => T["Tool." + char.ToUpperInvariant(direction[0]) + direction[1..]];
}

public sealed class Create3DViewTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MarginMm = 500;

    public override string Name => "create_3d_view";

    public override string Description =>
        "Proposes an isometric 3D view, optionally with a section box around the given elements (e.g. a room and its walls) " +
        "plus a 500 mm margin.";

    public override string ProgressLabel => "Checking the 3D view…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "name": { "type": ["string", "null"], "description": "View name. Null for Revit's automatic name." },
            "cropToElementIds": { "type": ["array", "null"], "items": { "type": ["integer", "string"] }, "description": "Elements to crop around, or null for the whole model." }
          },
          "required": ["name", "cropToElementIds"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        string? name = Name3D(document, arguments);
        int cropCount = 0;
        if (arguments.GetProperty("cropToElementIds") is { ValueKind: JsonValueKind.Array } ids)
        {
            cropCount = ids.GetArrayLength();
            foreach (JsonElement id in ids.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Number))
            {
                Box(document, id.GetInt64());
            }
        }

        return T.Format("Tool.ThreeDSummary",
            name is null ? "" : T.Format("Tool.NamedSuffix", name),
            cropCount == 0 ? "" : T.Format("Tool.SectionBoxSuffix", cropCount));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        string? name = Name3D(document, arguments);
        View3D view = View3D.CreateIsometric(document, ViewTools.ViewFamilyTypeId(T, document, ViewFamily.ThreeDimensional, "3D"));
        if (name is not null)
        {
            view.Name = name;
        }

        if (arguments.GetProperty("cropToElementIds") is { ValueKind: JsonValueKind.Array } ids && ids.GetArrayLength() > 0)
        {
            List<BoundingBoxXYZ> boxes = ids.EnumerateArray().Select(id => Box(document, id.GetInt64())).ToList();
            double margin = RevitRead.Feet(MarginMm);
            var marginVector = new XYZ(margin, margin, margin);
            view.SetSectionBox(new BoundingBoxXYZ
            {
                Min = new XYZ(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z)) - marginVector,
                Max = new XYZ(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z)) + marginVector,
            });
        }

        return new OperationResult(view.Id.Value, T.Format("Tool.ThreeDCreated", view.Id.Value, view.Name));
    }

    private string? Name3D(Document document, JsonElement arguments)
    {
        string? name = RevitRead.OptionalString(arguments, "name");
        return name is not null && DocumentationViews.NameTaken(document, name, ViewType.ThreeD)
            ? throw new ToolException(T.Format("Tool.ViewNameTaken", name))
            : name;
    }

    private BoundingBoxXYZ Box(Document document, long id) =>
        RevitRead.RequireElement(document, id).get_BoundingBox(null) ?? throw new ToolException(T.Format("Tool.NoBoundingBox", id));
}

public sealed class DimensionRoomTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double ParallelTolerance = 0.999;

    public override string Name => "dimension_room";

    public override string Description =>
        "Proposes dimensions of a room's clear width and depth in a plan view, between the inner faces of its opposite straight walls, " +
        "drawn through the room's location point. Works for rooms bounded by straight walls.";

    public override string ProgressLabel => "Checking the room dimensions…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewId": { "type": ["integer", "string"], "description": "Plan view ID (see the context's viewId), or $opN.elementId." },
            "roomId": { "type": ["integer", "string"], "description": "Room ID, or $opN.elementId." }
          },
          "required": ["viewId", "roomId"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        long? viewId = WriteArgs.IdOrReference(T, arguments, "viewId");
        long? roomId = WriteArgs.IdOrReference(T, arguments, "roomId");
        string viewName = viewId is null ? arguments.GetProperty("viewId").GetString()! : ViewTools.PlanView(T, document, viewId.Value).Name;
        string roomText = roomId is null ? arguments.GetProperty("roomId").GetString()! : PlacedRoom(document, roomId.Value).Number;
        return T.Format("Tool.RoomDimSummary", roomText, viewName);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        View view = ViewTools.PlanView(T, document, WriteArgs.Id(T, arguments, "viewId"));
        Room room = PlacedRoom(document, WriteArgs.Id(T, arguments, "roomId"));
        XYZ center = ((LocationPoint)room.Location).Point;

        List<(Wall Wall, Line Line)> walls = (room.GetBoundarySegments(new SpatialElementBoundaryOptions()) ?? [])
            .SelectMany(loop => loop)
            .Select(segment => document.GetElement(segment.ElementId) as Wall)
            .OfType<Wall>()
            .DistinctBy(wall => wall.Id.Value)
            .Select(wall => (Wall: wall, Line: (wall.Location as LocationCurve)?.Curve as Line))
            .Where(x => x.Line is not null)
            .Select(x => (x.Wall, x.Line!))
            .ToList();
        if (walls.Count == 0)
        {
            throw new ToolException(T.Format("Tool.RoomNoOppositeWalls", room.Number));
        }

        XYZ axisA = walls[0].Line.Direction;
        XYZ axisB = XYZ.BasisZ.CrossProduct(axisA).Normalize();

        var dimensions = new List<Dimension>();
        foreach ((XYZ wallDirection, XYZ across) in new[] { (axisA, axisB), (axisB, axisA) })
        {
            List<(Wall Wall, Line Line)> parallel = walls
                .Where(w => Math.Abs(w.Line.Direction.DotProduct(wallDirection)) > ParallelTolerance)
                .OrderBy(w => w.Line.Evaluate(0.5, true).DotProduct(across))
                .ToList();
            if (parallel.Count < 2)
            {
                continue;
            }

            var references = new ReferenceArray();
            references.Append(RoomSideFace(parallel[0].Wall, center));
            references.Append(RoomSideFace(parallel[^1].Wall, center));
            Line dimensionLine = Line.CreateBound(center, center + across);
            dimensions.Add(document.Create.NewDimension(view, dimensionLine, references));
        }

        if (dimensions.Count == 0)
        {
            throw new ToolException(T.Format("Tool.RoomNoOppositeWalls", room.Number));
        }

        return new OperationResult(null,
            T.Format("Tool.RoomDimCreated", dimensions.Count, room.Number, string.Join(" × ", dimensions.Select(d => d.ValueString))),
            dimensions.Select(d => d.Id.Value).ToList());
    }

    /// <summary>The wall's side face (interior or exterior shell) nearest the room point, i.e. the face towards the room.</summary>
    private Reference RoomSideFace(Wall wall, XYZ roomPoint)
    {
        Reference? best = null;
        double bestDistance = double.MaxValue;
        foreach (ShellLayerType side in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
        {
            foreach (Reference reference in HostObjectUtils.GetSideFaces(wall, side))
            {
                if (wall.GetGeometryObjectFromReference(reference) is PlanarFace face)
                {
                    double distance = Math.Abs((roomPoint - face.Origin).DotProduct(face.FaceNormal));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = reference;
                    }
                }
            }
        }

        return best ?? throw new ToolException(T.Format("Tool.RoomNoOppositeWalls", wall.Id.Value));
    }

    private Room PlacedRoom(Document document, long id)
    {
        Room room = document.GetElement(new ElementId(id)) as Room ?? throw new ToolException(T.Format("Tool.NotARoom", id));
        return room.Area <= 0 || room.Location is not LocationPoint
            ? throw new ToolException(T.Format("Tool.RoomNotPlaced", room.Number))
            : room;
    }
}

internal static class ViewTools
{
    public static ElementId ViewFamilyTypeId(UiText t, Document document, ViewFamily family, string familyName) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == family)?.Id
        ?? throw new ToolException(t.Format("Tool.NoViewFamilyType", familyName));

    public static View PlanView(UiText t, Document document, long id)
    {
        View view = document.GetElement(new ElementId(id)) as View ?? throw new ToolException(t.Format("Tool.NotAView", id));
        return view is ViewPlan { IsTemplate: false } ? view : throw new ToolException(t.Format("Tool.ViewNotPlan", view.Name));
    }
}

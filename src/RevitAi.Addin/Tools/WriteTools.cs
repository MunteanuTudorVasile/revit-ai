using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Localization;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Phase 2 write tools (docs/REVIT_TOOLS.md). All lengths/coordinates in mm, model coordinates (ADR-026).
// Validate() never changes the model; Apply() runs inside PlanExecutor's transaction.

public sealed class CreateWallTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MinLengthMm = 10;
    private const double DefaultHeightMm = 3000;
    private const double MaxHeightMm = 100_000;

    public override string Name => "create_wall";

    public override string Description =>
        "Proposes a straight wall between two points on a level. Height defaults to 3000 mm; wall type defaults to the project's default wall type.";

    public override string ProgressLabel => "Checking the wall…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "start": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false, "description": "Start point, model coordinates in mm." },
            "end": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false, "description": "End point, model coordinates in mm." },
            "levelId": { "type": "integer", "description": "Base level ID." },
            "wallTypeId": { "type": ["integer", "null"], "description": "Wall type ID (e.g. typeId of an existing wall). Null for the project default." },
            "heightMm": { "type": ["number", "null"], "description": "Unconnected height in mm. Null for 3000 mm." }
          },
          "required": ["start", "end", "levelId", "wallTypeId", "heightMm"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        return T.Format("Tool.WallSummary", inputs.Type.Name, inputs.Level.Name, WriteArgs.Mm(inputs.LengthMm), WriteArgs.Mm(inputs.HeightMm),
            inputs.Start.X, inputs.Start.Y, inputs.End.X, inputs.End.Y);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        Wall wall = Wall.Create(
            document,
            Line.CreateBound(WriteArgs.ToXyz(inputs.Start), WriteArgs.ToXyz(inputs.End)),
            inputs.Type.Id,
            inputs.Level.Id,
            RevitRead.Feet(inputs.HeightMm),
            0,
            false,
            false);
        return new OperationResult(wall.Id.Value,
            T.Format("Tool.WallCreated", wall.Id.Value, inputs.Type.Name, WriteArgs.Mm(inputs.LengthMm), inputs.Level.Name));
    }

    private Inputs Resolve(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());
        WallType type = WriteArgs.TypeOrDefault<WallType>(
            T,
            document,
            RevitRead.OptionalLong(arguments, "wallTypeId"),
            document.GetDefaultElementTypeId(ElementTypeGroup.WallType),
            "Tool.CatWalls");
        Point2 start = WriteArgs.Point(arguments, "start");
        Point2 end = WriteArgs.Point(arguments, "end");
        double length = Polygon2D.Distance(start, end);
        if (length < MinLengthMm)
        {
            throw new ToolException(T.Format("Tool.WallTooShort", WriteArgs.Mm(length), WriteArgs.Mm(MinLengthMm)));
        }

        double height = WriteArgs.OptionalNumber(arguments, "heightMm") ?? DefaultHeightMm;
        if (height <= 0 || height > MaxHeightMm)
        {
            throw new ToolException(T.Format("Tool.WallHeightRange", WriteArgs.Mm(MaxHeightMm)));
        }

        return new Inputs(level, type, start, end, length, height);
    }

    private sealed record Inputs(Level Level, WallType Type, Point2 Start, Point2 End, double LengthMm, double HeightMm);
}

public sealed class ModifyWallTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MinLengthMm = 10;

    public override string Name => "modify_wall";

    public override string Description =>
        "Proposes making a straight wall longer or shorter by moving one of its ends along the wall. " +
        "Positive distanceMm extends, negative shortens. Connected walls may adjust their joins.";

    public override string ProgressLabel => "Checking the wall change…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "wallId": { "type": ["integer", "string"], "description": "Wall ID, or $opN.elementId for a wall created earlier in this plan." },
            "end": { "type": "string", "enum": ["start", "end"], "description": "Which end of the wall moves." },
            "distanceMm": { "type": "number", "description": "Change in length in mm. Positive extends, negative shortens." }
          },
          "required": ["wallId", "end", "distanceMm"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        double distance = arguments.GetProperty("distanceMm").GetDouble();
        string end = arguments.GetProperty("end").GetString()!;
        if (distance == 0)
        {
            throw new ToolException(T["Tool.ModifyZero"]);
        }

        string endText = T[end == "end" ? "Tool.EndEnd" : "Tool.EndStart"];
        long? wallId = WriteArgs.IdOrReference(T, arguments, "wallId");
        if (wallId is null)
        {
            return T.Format("Tool.ModifyReferenceSummary", arguments.GetProperty("wallId").GetString()!, distance, endText);
        }

        (double oldLength, double newLength, _, _) = Compute(document, wallId.Value, end, distance);
        return T.Format("Tool.ModifySummary", wallId, WriteArgs.Mm(oldLength), WriteArgs.Mm(newLength), endText);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        long wallId = WriteArgs.Id(T, arguments, "wallId");
        string end = arguments.GetProperty("end").GetString()!;
        (double oldLength, double newLength, XYZ start, XYZ finish) =
            Compute(document, wallId, end, arguments.GetProperty("distanceMm").GetDouble());

        var location = (LocationCurve)WriteArgs.Wall(T, document, wallId).Location;
        location.Curve = Line.CreateBound(start, finish);
        return new OperationResult(wallId, T.Format("Tool.ModifyDone", wallId, WriteArgs.Mm(oldLength), WriteArgs.Mm(newLength)));
    }

    private (double OldLengthMm, double NewLengthMm, XYZ Start, XYZ End) Compute(Document document, long wallId, string end, double distanceMm)
    {
        Line line = WriteArgs.WallLine(T, WriteArgs.Wall(T, document, wallId));
        double oldLength = UnitUtils.ConvertFromInternalUnits(line.Length, UnitTypeId.Millimeters);
        double newLength = oldLength + distanceMm;
        if (newLength < MinLengthMm)
        {
            throw new ToolException(T.Format("Tool.ModifyTooShort", wallId, WriteArgs.Mm(oldLength), WriteArgs.Mm(Math.Abs(distanceMm))));
        }

        XYZ offset = line.Direction * RevitRead.Feet(distanceMm);
        return end == "end"
            ? (oldLength, newLength, line.GetEndPoint(0), line.GetEndPoint(1) + offset)
            : (oldLength, newLength, line.GetEndPoint(0) - offset, line.GetEndPoint(1));
    }
}

public sealed class CreateRoomTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "create_room";

    public override string Description =>
        "Proposes a room at a point on a level. The point must be inside an area enclosed by walls (existing or created earlier in this plan); " +
        "otherwise the plan fails and nothing is changed.";

    public override string ProgressLabel => "Checking the room…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "levelId": { "type": "integer", "description": "Level ID." },
            "point": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false, "description": "A point inside the enclosed area, model coordinates in mm." },
            "name": { "type": ["string", "null"], "description": "Room name, e.g. Bedroom. Null to keep Revit's default." },
            "number": { "type": ["string", "null"], "description": "Room number. Null for automatic numbering." }
          },
          "required": ["levelId", "point", "name", "number"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());
        Point2 point = WriteArgs.Point(arguments, "point");
        string? name = RevitRead.OptionalString(arguments, "name");
        return name is null
            ? T.Format("Tool.RoomSummary", level.Name, point.X, point.Y)
            : T.Format("Tool.RoomSummaryNamed", name, level.Name, point.X, point.Y);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());
        Point2 point = WriteArgs.Point(arguments, "point");
        Room room = document.Create.NewRoom(level, new UV(RevitRead.Feet(point.X), RevitRead.Feet(point.Y)));

        if (RevitRead.OptionalString(arguments, "name") is { } name)
        {
            room.Name = name;
        }

        if (RevitRead.OptionalString(arguments, "number") is { } number)
        {
            room.Number = number;
        }

        document.Regenerate();
        if (room.Area <= 0)
        {
            throw new ToolException(T.Format("Tool.RoomNotEnclosed", point.X, point.Y));
        }

        return new OperationResult(room.Id.Value, T.Format("Tool.RoomCreated", room.Number, room.Name, RevitRead.M2(room.Area), level.Name));
    }
}

public sealed class CreateDoorTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "create_door";

    public override string Description =>
        "Proposes a door hosted in a straight wall, centred at a distance from the wall's start point. Door type defaults to the project's default door type.";

    public override string ProgressLabel => "Checking the door…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "wallId": { "type": ["integer", "string"], "description": "Host wall ID, or $opN.elementId for a wall created earlier in this plan." },
            "doorTypeId": { "type": ["integer", "null"], "description": "Door type ID (e.g. typeId of an existing door). Null for the project default." },
            "offsetAlongWallMm": { "type": "number", "description": "Distance in mm from the wall's start point to the door's centre." }
          },
          "required": ["wallId", "doorTypeId", "offsetAlongWallMm"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        FamilySymbol type = WriteArgs.FamilyType(T, document, RevitRead.OptionalLong(arguments, "doorTypeId"), BuiltInCategory.OST_Doors, "Tool.CatDoors");
        return HostedPlacement.Validate(T, document, arguments, T["Tool.Door"], type);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        FamilySymbol type = WriteArgs.FamilyType(T, document, RevitRead.OptionalLong(arguments, "doorTypeId"), BuiltInCategory.OST_Doors, "Tool.CatDoors");
        FamilyInstance door = HostedPlacement.Place(T, document, arguments, type, T["Tool.Door"]);
        return new OperationResult(door.Id.Value, T.Format("Tool.DoorPlaced", door.Id.Value, $"{type.FamilyName}: {type.Name}", door.Host.Id.Value));
    }
}

public sealed class CreateWindowTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MaxSillMm = 10_000;

    public override string Name => "create_window";

    public override string Description =>
        "Proposes a window hosted in a straight wall, centred at a distance from the wall's start point, optionally with a sill height. " +
        "Window type defaults to the project's default window type.";

    public override string ProgressLabel => "Checking the window…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "wallId": { "type": ["integer", "string"], "description": "Host wall ID, or $opN.elementId for a wall created earlier in this plan." },
            "windowTypeId": { "type": ["integer", "null"], "description": "Window type ID (e.g. typeId of an existing window). Null for the project default." },
            "offsetAlongWallMm": { "type": "number", "description": "Distance in mm from the wall's start point to the window's centre." },
            "sillHeightMm": { "type": ["number", "null"], "description": "Sill height in mm. Null for the type's default." }
          },
          "required": ["wallId", "windowTypeId", "offsetAlongWallMm", "sillHeightMm"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        FamilySymbol type = WriteArgs.FamilyType(T, document, RevitRead.OptionalLong(arguments, "windowTypeId"), BuiltInCategory.OST_Windows, "Tool.CatWindows");
        double? sill = Sill(arguments);
        return HostedPlacement.Validate(T, document, arguments, T["Tool.Window"], type)
               + (sill is null ? "" : T.Format("Tool.SillSuffix", WriteArgs.Mm(sill.Value)));
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        FamilySymbol type = WriteArgs.FamilyType(T, document, RevitRead.OptionalLong(arguments, "windowTypeId"), BuiltInCategory.OST_Windows, "Tool.CatWindows");
        FamilyInstance window = HostedPlacement.Place(T, document, arguments, type, T["Tool.Window"]);

        if (Sill(arguments) is { } sill)
        {
            Parameter? parameter = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (parameter is null || parameter.IsReadOnly)
            {
                throw new ToolException(T.Format("Tool.SillNotEditable", type.Name));
            }

            parameter.Set(RevitRead.Feet(sill));
        }

        return new OperationResult(window.Id.Value, T.Format("Tool.WindowPlaced", window.Id.Value, $"{type.FamilyName}: {type.Name}", window.Host.Id.Value));
    }

    private double? Sill(JsonElement arguments)
    {
        double? sill = WriteArgs.OptionalNumber(arguments, "sillHeightMm");
        return sill is < 0 or > MaxSillMm
            ? throw new ToolException(T.Format("Tool.SillRange", WriteArgs.Mm(MaxSillMm)))
            : sill;
    }
}

/// <summary>Shared placement of doors and windows in a straight host wall.</summary>
internal static class HostedPlacement
{
    public static string Validate(UiText t, Document document, JsonElement arguments, string what, FamilySymbol type)
    {
        double offset = arguments.GetProperty("offsetAlongWallMm").GetDouble();
        long? wallId = WriteArgs.IdOrReference(t, arguments, "wallId");
        string host = wallId is null ? arguments.GetProperty("wallId").GetString()! : t.Format("Tool.WallRef", wallId);

        if (wallId is not null)
        {
            CheckOffset(t, WriteArgs.WallLine(t, WriteArgs.Wall(t, document, wallId.Value)), offset);
        }

        return t.Format("Tool.HostedSummary", what, $"{type.FamilyName}: {type.Name}", host, WriteArgs.Mm(offset));
    }

    public static FamilyInstance Place(UiText t, Document document, JsonElement arguments, FamilySymbol type, string what)
    {
        Wall wall = WriteArgs.Wall(t, document, WriteArgs.Id(t, arguments, "wallId"));
        Line line = WriteArgs.WallLine(t, wall);
        double offset = arguments.GetProperty("offsetAlongWallMm").GetDouble();
        double lengthMm = CheckOffset(t, line, offset);

        if (!type.IsActive)
        {
            type.Activate();
        }

        Level level = document.GetElement(wall.LevelId) as Level
            ?? throw new ToolException(t.Format("Tool.WallNoLevel", wall.Id.Value));
        XYZ point = line.Evaluate(offset / lengthMm, true);
        FamilyInstance instance = document.Create.NewFamilyInstance(point, type, wall, level, StructuralType.NonStructural);

        return instance.Host?.Id == wall.Id
            ? instance
            : throw new ToolException(t.Format("Tool.NotHosted", what, wall.Id.Value));
    }

    private static double CheckOffset(UiText t, Line line, double offsetMm)
    {
        double lengthMm = UnitUtils.ConvertFromInternalUnits(line.Length, UnitTypeId.Millimeters);
        return offsetMm < 0 || offsetMm > lengthMm
            ? throw new ToolException(t.Format("Tool.OffsetOutside", WriteArgs.Mm(offsetMm), WriteArgs.Mm(lengthMm)))
            : lengthMm;
    }
}

public sealed class CreateFloorTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MinEdgeMm = 10;
    private const double MinAreaMm2 = 10_000; // 0.01 m²

    public override string Name => "create_floor";

    public override string Description =>
        "Proposes a floor on a level from a closed boundary of at least 3 points (the last point connects back to the first). " +
        "The boundary must not cross itself. Floor type defaults to the project's default floor type.";

    public override string ProgressLabel => "Checking the floor…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "levelId": { "type": "integer", "description": "Level ID." },
            "boundary": {
              "type": "array",
              "items": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "additionalProperties": false },
              "description": "Boundary corners in order, model coordinates in mm. Do not repeat the first point at the end."
            },
            "floorTypeId": { "type": ["integer", "null"], "description": "Floor type ID (e.g. typeId of an existing floor). Null for the project default." }
          },
          "required": ["levelId", "boundary", "floorTypeId"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        return T.Format("Tool.FloorSummary", inputs.Type.Name, inputs.Level.Name, inputs.Boundary.Count, Polygon2D.Area(inputs.Boundary) / 1_000_000);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Inputs inputs = Resolve(document, arguments);
        var loop = new CurveLoop();
        for (int i = 0; i < inputs.Boundary.Count; i++)
        {
            loop.Append(Line.CreateBound(
                WriteArgs.ToXyz(inputs.Boundary[i]),
                WriteArgs.ToXyz(inputs.Boundary[(i + 1) % inputs.Boundary.Count])));
        }

        Floor floor = Floor.Create(document, [loop], inputs.Type.Id, inputs.Level.Id);
        return new OperationResult(floor.Id.Value,
            T.Format("Tool.FloorCreated", floor.Id.Value, inputs.Type.Name, Polygon2D.Area(inputs.Boundary) / 1_000_000, inputs.Level.Name));
    }

    private Inputs Resolve(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(T, document, arguments.GetProperty("levelId").GetInt64());
        FloorType type = WriteArgs.TypeOrDefault<FloorType>(
            T,
            document,
            RevitRead.OptionalLong(arguments, "floorTypeId"),
            document.GetDefaultElementTypeId(ElementTypeGroup.FloorType),
            "Tool.CatFloors");

        List<Point2> boundary = arguments.GetProperty("boundary").EnumerateArray()
            .Select(p => new Point2(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble()))
            .ToList();
        if (boundary.Count < 3)
        {
            throw new ToolException(T["Tool.FloorTooFewPoints"]);
        }

        if (Polygon2D.MinEdgeLength(boundary) < MinEdgeMm)
        {
            throw new ToolException(T.Format("Tool.FloorShortEdge", WriteArgs.Mm(MinEdgeMm)));
        }

        if (!Polygon2D.IsSimple(boundary))
        {
            throw new ToolException(T["Tool.FloorSelfIntersects"]);
        }

        if (Polygon2D.Area(boundary) < MinAreaMm2)
        {
            throw new ToolException(T["Tool.FloorNoArea"]);
        }

        return new Inputs(level, type, boundary);
    }

    private sealed record Inputs(Level Level, FloorType Type, IReadOnlyList<Point2> Boundary);
}

public sealed class MoveElementsTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const double MaxDistanceMm = 100_000;
    private const int MaxListed = 5;

    public override string Name => "move_elements";

    public override string Description =>
        "Proposes moving elements by a horizontal distance (dxMm, dyMm in model coordinates). Walls move with their hosted doors " +
        "and windows; joined walls adjust. To make a room wider, move one of its bounding walls perpendicular to itself " +
        "(get_room_boundary gives the walls and their positions). Pinned elements are refused.";

    public override string ProgressLabel => "Checking the move…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": "array", "items": { "type": ["integer", "string"] }, "description": "Elements to move, or $opN.elementId references." },
            "dxMm": { "type": "number", "description": "Move along model X, in mm." },
            "dyMm": { "type": "number", "description": "Move along model Y, in mm." }
          },
          "required": ["elementIds", "dxMm", "dyMm"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        (double dx, double dy) = Offset(arguments);
        List<JsonElement> ids = Ids(arguments);
        var described = new List<string>();
        foreach (JsonElement id in ids)
        {
            if (id.ValueKind == JsonValueKind.Number)
            {
                Element element = Movable(document, id.GetInt64());
                described.Add($"{element.Category?.Name} {element.Id.Value}");
            }
            else
            {
                described.Add(id.GetString()!);
            }
        }

        string list = string.Join(", ", described.Take(MaxListed)) + (described.Count > MaxListed ? ", …" : "");
        return T.Format("Tool.MoveSummary", ids.Count, dx, dy, list);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        (double dx, double dy) = Offset(arguments);
        List<ElementId> ids = Ids(arguments).Select(id => Movable(document, id.GetInt64()).Id).ToList();
        ElementTransformUtils.MoveElements(document, ids, new XYZ(RevitRead.Feet(dx), RevitRead.Feet(dy), 0));
        return new OperationResult(ids[0].Value, T.Format("Tool.MoveDone", ids.Count, dx, dy), ids.Skip(1).Select(id => id.Value).ToList());
    }

    private (double Dx, double Dy) Offset(JsonElement arguments)
    {
        double dx = arguments.GetProperty("dxMm").GetDouble();
        double dy = arguments.GetProperty("dyMm").GetDouble();
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        return distance < 0.5 ? throw new ToolException(T["Tool.MoveZero"])
            : distance > MaxDistanceMm ? throw new ToolException(T.Format("Tool.MoveTooFar", WriteArgs.Mm(MaxDistanceMm)))
            : (dx, dy);
    }

    private List<JsonElement> Ids(JsonElement arguments)
    {
        List<JsonElement> ids = arguments.GetProperty("elementIds").EnumerateArray().ToList();
        return ids.Count == 0 ? throw new ToolException(T["Tool.MoveNeedsElements"]) : ids;
    }

    private Element Movable(Document document, long id)
    {
        Element element = RevitRead.RequireElement(document, id);
        return element.Pinned ? throw new ToolException(T.Format("Tool.Pinned", id)) : element;
    }
}

public sealed class DeleteElementsTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int MaxElements = 200;
    private const int MaxListed = 5;

    public override string Name => "delete_elements";

    public override string Description =>
        "Proposes deleting model elements or annotations (DESTRUCTIVE). Only use when the user explicitly asks to delete. " +
        "Revit also deletes dependent elements (e.g. doors in a deleted wall); the user must preview the plan and explicitly confirm " +
        $"before Apply. Types, views, sheets, levels, grids and pinned elements are refused. At most {MaxElements} elements.";

    public override string ProgressLabel => "Checking the deletion…";

    public override RiskLevel Risk => RiskLevel.Destructive;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": "array", "items": { "type": ["integer", "string"] }, "description": "Elements to delete, or $opN.elementId references." }
          },
          "required": ["elementIds"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        List<JsonElement> ids = Ids(arguments);
        var described = new List<string>();
        foreach (JsonElement id in ids)
        {
            if (id.ValueKind == JsonValueKind.Number)
            {
                Element element = Deletable(document, id.GetInt64());
                described.Add($"{element.Category?.Name} {element.Id.Value}");
            }
            else
            {
                described.Add(id.GetString()!);
            }
        }

        string list = string.Join(", ", described.Take(MaxListed)) + (described.Count > MaxListed ? ", …" : "");
        return T.Format("Tool.DeleteSummary", ids.Count, list);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        List<ElementId> requested = Ids(arguments).Select(id => Deletable(document, id.GetInt64()).Id).Distinct().ToList();
        List<long> deleted = document.Delete(requested).Select(id => id.Value).ToList();
        return new OperationResult(null, T.Format("Tool.DeleteDone", deleted.Count, Math.Max(0, deleted.Count - requested.Count)), deleted);
    }

    private List<JsonElement> Ids(JsonElement arguments)
    {
        List<JsonElement> ids = arguments.GetProperty("elementIds").EnumerateArray().ToList();
        return ids.Count == 0 ? throw new ToolException(T["Tool.MoveNeedsElements"])
            : ids.Count > MaxElements ? throw new ToolException(T.Format("Tool.DeleteTooMany", MaxElements))
            : ids;
    }

    /// <summary>Only model elements and annotations; never types, views, sheets, levels, grids or pinned elements.</summary>
    private Element Deletable(Document document, long id)
    {
        Element element = RevitRead.RequireElement(document, id);
        if (element.Pinned)
        {
            throw new ToolException(T.Format("Tool.Pinned", id));
        }

        bool allowed = element is not (ElementType or View or Level or Grid)
                       && element.Category is { } category
                       && category.CategoryType is CategoryType.Model or CategoryType.Annotation
                       && category.Id.Value is not ((long)BuiltInCategory.OST_Levels or (long)BuiltInCategory.OST_Grids);
        return allowed ? element : throw new ToolException(T.Format("Tool.DeleteNotAllowed", id, element.Category?.Name ?? element.GetType().Name));
    }
}

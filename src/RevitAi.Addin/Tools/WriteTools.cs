using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Phase 2 write tools (docs/REVIT_TOOLS.md). All lengths/coordinates in mm, model coordinates (ADR-026).
// Validate() never changes the model; Apply() runs inside PlanExecutor's transaction.

public sealed class CreateWallTool(RevitDispatcher dispatcher) : RevitWriteTool(dispatcher)
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
        return $"Create wall '{inputs.Type.Name}' on {inputs.Level.Name}, {WriteArgs.Mm(inputs.LengthMm)} long, " +
               $"{WriteArgs.Mm(inputs.HeightMm)} high, from ({inputs.Start.X:0}, {inputs.Start.Y:0}) to ({inputs.End.X:0}, {inputs.End.Y:0})";
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
            $"Created wall {wall.Id.Value} ('{inputs.Type.Name}', {WriteArgs.Mm(inputs.LengthMm)}) on {inputs.Level.Name}");
    }

    private static Inputs Resolve(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(document, arguments.GetProperty("levelId").GetInt64());
        WallType type = WriteArgs.TypeOrDefault<WallType>(
            document,
            RevitRead.OptionalLong(arguments, "wallTypeId"),
            document.GetDefaultElementTypeId(ElementTypeGroup.WallType),
            "wall type");
        Point2 start = WriteArgs.Point(arguments, "start");
        Point2 end = WriteArgs.Point(arguments, "end");
        double length = Polygon2D.Distance(start, end);
        if (length < MinLengthMm)
        {
            throw new ToolException($"The wall would be {length:0} mm long; walls must be at least {MinLengthMm:0} mm.");
        }

        double height = WriteArgs.OptionalNumber(arguments, "heightMm") ?? DefaultHeightMm;
        if (height <= 0 || height > MaxHeightMm)
        {
            throw new ToolException($"Wall height must be between 0 and {MaxHeightMm:0} mm.");
        }

        return new Inputs(level, type, start, end, length, height);
    }

    private sealed record Inputs(Level Level, WallType Type, Point2 Start, Point2 End, double LengthMm, double HeightMm);
}

public sealed class ModifyWallTool(RevitDispatcher dispatcher) : RevitWriteTool(dispatcher)
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
            throw new ToolException("distanceMm must not be 0.");
        }

        long? wallId = WriteArgs.IdOrReference(arguments, "wallId");
        if (wallId is null)
        {
            return $"Change the length of {arguments.GetProperty("wallId").GetString()} by {distance:+0;-0} mm at its {end}";
        }

        (double oldLength, double newLength, _, _) = Compute(document, wallId.Value, end, distance);
        return $"Wall {wallId}: {WriteArgs.Mm(oldLength)} → {WriteArgs.Mm(newLength)} (moves its {end} point)";
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        long wallId = WriteArgs.Id(arguments, "wallId");
        string end = arguments.GetProperty("end").GetString()!;
        (double oldLength, double newLength, XYZ start, XYZ finish) =
            Compute(document, wallId, end, arguments.GetProperty("distanceMm").GetDouble());

        var location = (LocationCurve)WriteArgs.Wall(document, wallId).Location;
        location.Curve = Line.CreateBound(start, finish);
        return new OperationResult(wallId, $"Wall {wallId}: {WriteArgs.Mm(oldLength)} → {WriteArgs.Mm(newLength)}");
    }

    private static (double OldLengthMm, double NewLengthMm, XYZ Start, XYZ End) Compute(Document document, long wallId, string end, double distanceMm)
    {
        Line line = WriteArgs.WallLine(WriteArgs.Wall(document, wallId));
        double oldLength = UnitUtils.ConvertFromInternalUnits(line.Length, UnitTypeId.Millimeters);
        double newLength = oldLength + distanceMm;
        if (newLength < MinLengthMm)
        {
            throw new ToolException($"Wall {wallId} is {WriteArgs.Mm(oldLength)} long; it can't be shortened by {Math.Abs(distanceMm):0} mm.");
        }

        XYZ offset = line.Direction * RevitRead.Feet(distanceMm);
        return end == "end"
            ? (oldLength, newLength, line.GetEndPoint(0), line.GetEndPoint(1) + offset)
            : (oldLength, newLength, line.GetEndPoint(0) - offset, line.GetEndPoint(1));
    }
}

public sealed class CreateRoomTool(RevitDispatcher dispatcher) : RevitWriteTool(dispatcher)
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
        Level level = WriteArgs.Level(document, arguments.GetProperty("levelId").GetInt64());
        Point2 point = WriteArgs.Point(arguments, "point");
        string? name = RevitRead.OptionalString(arguments, "name");
        return $"Create room{(name is null ? "" : $" '{name}'")} on {level.Name} at ({point.X:0}, {point.Y:0})";
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(document, arguments.GetProperty("levelId").GetInt64());
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
            throw new ToolException(
                $"The point ({point.X:0}, {point.Y:0}) isn't inside an area enclosed by walls or room separation lines, so the room would not be enclosed.");
        }

        return new OperationResult(room.Id.Value,
            $"Created room {room.Number} '{room.Name}' ({RevitRead.M2(room.Area)} m²) on {level.Name}");
    }
}

public sealed class CreateDoorTool(RevitDispatcher dispatcher) : RevitWriteTool(dispatcher)
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
        FamilySymbol type = WriteArgs.FamilyType(document, RevitRead.OptionalLong(arguments, "doorTypeId"), BuiltInCategory.OST_Doors, "door type");
        return HostedPlacement.Validate(document, arguments, "door", type);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        FamilySymbol type = WriteArgs.FamilyType(document, RevitRead.OptionalLong(arguments, "doorTypeId"), BuiltInCategory.OST_Doors, "door type");
        FamilyInstance door = HostedPlacement.Place(document, arguments, type, "door");
        return new OperationResult(door.Id.Value,
            $"Placed door {door.Id.Value} ('{type.FamilyName}: {type.Name}') in wall {door.Host.Id.Value}");
    }
}

public sealed class CreateWindowTool(RevitDispatcher dispatcher) : RevitWriteTool(dispatcher)
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
        FamilySymbol type = WriteArgs.FamilyType(document, RevitRead.OptionalLong(arguments, "windowTypeId"), BuiltInCategory.OST_Windows, "window type");
        double? sill = Sill(arguments);
        return HostedPlacement.Validate(document, arguments, "window", type) + (sill is null ? "" : $", sill {WriteArgs.Mm(sill.Value)}");
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        FamilySymbol type = WriteArgs.FamilyType(document, RevitRead.OptionalLong(arguments, "windowTypeId"), BuiltInCategory.OST_Windows, "window type");
        FamilyInstance window = HostedPlacement.Place(document, arguments, type, "window");

        if (Sill(arguments) is { } sill)
        {
            Parameter? parameter = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (parameter is null || parameter.IsReadOnly)
            {
                throw new ToolException($"Window type '{type.Name}' has no editable sill height.");
            }

            parameter.Set(RevitRead.Feet(sill));
        }

        return new OperationResult(window.Id.Value,
            $"Placed window {window.Id.Value} ('{type.FamilyName}: {type.Name}') in wall {window.Host.Id.Value}");
    }

    private static double? Sill(JsonElement arguments)
    {
        double? sill = WriteArgs.OptionalNumber(arguments, "sillHeightMm");
        return sill is < 0 or > MaxSillMm
            ? throw new ToolException($"sillHeightMm must be between 0 and {MaxSillMm:0} mm.")
            : sill;
    }
}

/// <summary>Shared placement of doors and windows in a straight host wall.</summary>
internal static class HostedPlacement
{
    public static string Validate(Document document, JsonElement arguments, string what, FamilySymbol type)
    {
        double offset = arguments.GetProperty("offsetAlongWallMm").GetDouble();
        long? wallId = WriteArgs.IdOrReference(arguments, "wallId");
        string host = wallId is null ? arguments.GetProperty("wallId").GetString()! : $"wall {wallId}";

        if (wallId is not null)
        {
            CheckOffset(WriteArgs.WallLine(WriteArgs.Wall(document, wallId.Value)), offset);
        }

        return $"Place {what} '{type.FamilyName}: {type.Name}' in {host}, centred {WriteArgs.Mm(offset)} from its start";
    }

    public static FamilyInstance Place(Document document, JsonElement arguments, FamilySymbol type, string what)
    {
        Wall wall = WriteArgs.Wall(document, WriteArgs.Id(arguments, "wallId"));
        Line line = WriteArgs.WallLine(wall);
        double offset = arguments.GetProperty("offsetAlongWallMm").GetDouble();
        double lengthMm = CheckOffset(line, offset);

        if (!type.IsActive)
        {
            type.Activate();
        }

        Level level = document.GetElement(wall.LevelId) as Level
            ?? throw new ToolException($"Wall {wall.Id.Value} has no base level.");
        XYZ point = line.Evaluate(offset / lengthMm, true);
        FamilyInstance instance = document.Create.NewFamilyInstance(point, type, wall, level, StructuralType.NonStructural);

        return instance.Host?.Id == wall.Id
            ? instance
            : throw new ToolException($"The {what} could not be hosted in wall {wall.Id.Value}.");
    }

    private static double CheckOffset(Line line, double offsetMm)
    {
        double lengthMm = UnitUtils.ConvertFromInternalUnits(line.Length, UnitTypeId.Millimeters);
        return offsetMm < 0 || offsetMm > lengthMm
            ? throw new ToolException($"offsetAlongWallMm {offsetMm:0} is outside the wall, which is {lengthMm:0} mm long.")
            : lengthMm;
    }
}

public sealed class CreateFloorTool(RevitDispatcher dispatcher) : RevitWriteTool(dispatcher)
{
    private const double MinEdgeMm = 10;

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
        return $"Create floor '{inputs.Type.Name}' on {inputs.Level.Name}, {inputs.Boundary.Count} corners, " +
               $"{Polygon2D.Area(inputs.Boundary) / 1_000_000:0.##} m²";
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
            $"Created floor {floor.Id.Value} ('{inputs.Type.Name}', {Polygon2D.Area(inputs.Boundary) / 1_000_000:0.##} m²) on {inputs.Level.Name}");
    }

    private static Inputs Resolve(Document document, JsonElement arguments)
    {
        Level level = WriteArgs.Level(document, arguments.GetProperty("levelId").GetInt64());
        FloorType type = WriteArgs.TypeOrDefault<FloorType>(
            document,
            RevitRead.OptionalLong(arguments, "floorTypeId"),
            document.GetDefaultElementTypeId(ElementTypeGroup.FloorType),
            "floor type");

        List<Point2> boundary = arguments.GetProperty("boundary").EnumerateArray()
            .Select(p => new Point2(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble()))
            .ToList();
        if (boundary.Count < 3)
        {
            throw new ToolException("A floor boundary needs at least 3 points.");
        }

        if (Polygon2D.MinEdgeLength(boundary) < MinEdgeMm)
        {
            throw new ToolException($"Boundary edges must be at least {MinEdgeMm:0} mm long (check for repeated points).");
        }

        if (!Polygon2D.IsSimple(boundary))
        {
            throw new ToolException("The boundary crosses or touches itself.");
        }

        return new Inputs(level, type, boundary);
    }

    private sealed record Inputs(Level Level, FloorType Type, IReadOnlyList<Point2> Boundary);
}

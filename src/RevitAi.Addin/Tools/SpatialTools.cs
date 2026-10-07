using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

public sealed class FindNearbyElementsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const double DefaultRadiusMm = 1000;
    private const double MaxRadiusMm = 20_000;
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    public override string Name => "find_nearby_elements";

    public override string Description =>
        "Finds model elements whose bounding box is within radiusMm of an element's bounding box (default 1000 mm, max 20000 mm), " +
        "optionally of one category. Sorted by distance between bounding-box centres. " +
        "Includes elements touching it, e.g. walls joined to a wall or the wall hosting a door.";

    public override string ProgressLabel => "Looking for nearby elements…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementId": { "type": "integer", "description": "The element to search around." },
            "radiusMm": { "type": ["number", "null"], "description": "Search distance in mm. Null for 1000 mm." },
            "category": { "type": ["string", "null"], "description": "Only this category, e.g. Walls, Doors. Null for all model categories." },
            "limit": { "type": ["integer", "null"], "description": "Maximum elements to return. Null for 30." }
          },
          "required": ["elementId", "radiusMm", "category", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        long elementId = arguments.GetProperty("elementId").GetInt64();
        Element element = RevitRead.RequireElement(document, elementId);
        BoundingBoxXYZ box = element.get_BoundingBox(null)
            ?? throw new ToolException($"Element {elementId} has no geometry to search around.");

        double radiusMm = Math.Clamp(WriteArgs.OptionalNumber(arguments, "radiusMm") ?? DefaultRadiusMm, 0, MaxRadiusMm);
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        double r = RevitRead.Feet(radiusMm);
        var outline = new Outline(box.Min - new XYZ(r, r, r), box.Max + new XYZ(r, r, r));

        var collector = new FilteredElementCollector(document).WhereElementIsNotElementType();
        if (RevitRead.OptionalString(arguments, "category") is { } categoryName)
        {
            collector = collector.OfCategoryId(RevitRead.RequireCategory(document, categoryName).Id);
        }

        XYZ center = (box.Min + box.Max) / 2;
        List<NearbyElement> nearby = collector
            .WherePasses(new BoundingBoxIntersectsFilter(outline))
            .Where(e => e.Id != element.Id && e.Category?.CategoryType == CategoryType.Model)
            .Select(e => (Element: e, Box: e.get_BoundingBox(null)))
            .Where(x => x.Box is not null)
            .Select(x => new NearbyElement(
                RevitRead.Summarize(x.Element),
                RevitRead.Mm(center.DistanceTo((x.Box!.Min + x.Box.Max) / 2))))
            .OrderBy(n => n.CenterDistanceMm)
            .ToList();

        return new NearbyElementsResult(elementId, radiusMm, nearby.Count, nearby.Count > limit, nearby.Take(limit).ToList());
    }
}

public sealed class GetElementRoomTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "get_element_room";

    public override string Description =>
        "Returns the room an element is in. Doors and windows return the rooms on both sides (fromRoom/toRoom). " +
        "Walls usually sit between rooms; use get_room_boundary or find_nearby_elements for those.";

    public override string ProgressLabel => "Finding the element's room…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementId": { "type": "integer", "description": "Element ID." }
          },
          "required": ["elementId"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        long elementId = arguments.GetProperty("elementId").GetInt64();
        Element element = RevitRead.RequireElement(document, elementId);

        if (element is FamilyInstance { } instance && (instance.FromRoom is not null || instance.ToRoom is not null))
        {
            return new ElementRoomResult(elementId, null, Describe(instance.FromRoom), Describe(instance.ToRoom), null);
        }

        XYZ? point = element.Location switch
        {
            LocationPoint p => p.Point,
            LocationCurve c => c.Curve.Evaluate(0.5, true),
            _ => element.get_BoundingBox(null) is { } box ? (box.Min + box.Max) / 2 : null,
        };

        if (point is null)
        {
            return new ElementRoomResult(elementId, null, null, null, "The element has no location.");
        }

        Room? room = document.GetRoomAtPoint(point);
        return new ElementRoomResult(elementId, Describe(room), null, null,
            room is null ? "No room at the element's location (it may be outside rooms, or a wall between rooms)." : null);
    }

    private static RoomInfo? Describe(Room? room) => room is null ? null : RevitRead.DescribeRoom(room);
}

public sealed class GetRoomBoundaryTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "get_room_boundary";

    public override string Description =>
        "Returns a room's boundary: one or more closed loops of segments (outer loop first), each with the bounding element " +
        "(usually a wall) and its start, end and length in mm.";

    public override string ProgressLabel => "Reading the room boundary…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "roomId": { "type": "integer", "description": "Room element ID." }
          },
          "required": ["roomId"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        long roomId = arguments.GetProperty("roomId").GetInt64();
        Room room = RevitRead.RequireElement(document, roomId) as Room
            ?? throw new ToolException($"Element {roomId} is not a room.");

        IList<IList<BoundarySegment>>? loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
        if (loops is null || loops.Count == 0)
        {
            return new RoomBoundaryResult(RevitRead.DescribeRoom(room), [], "The room is not placed or not enclosed, so it has no boundary.");
        }

        List<IReadOnlyList<BoundarySegmentInfo>> result = loops
            .Select(loop => (IReadOnlyList<BoundarySegmentInfo>)loop.Select(segment =>
            {
                Curve curve = segment.GetCurve();
                Element? bounding = document.GetElement(segment.ElementId);
                return new BoundarySegmentInfo(
                    bounding?.Id.Value,
                    bounding?.Category?.Name,
                    RevitRead.Point(curve.GetEndPoint(0)),
                    RevitRead.Point(curve.GetEndPoint(1)),
                    RevitRead.Mm(curve.Length));
            }).ToList())
            .ToList();

        return new RoomBoundaryResult(RevitRead.DescribeRoom(room), result, null);
    }
}

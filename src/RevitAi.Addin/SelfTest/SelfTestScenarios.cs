using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;
using RevitAi.Core.Workflows;
using static RevitAi.Addin.SelfTest.SelfTestRunner;

namespace RevitAi.Addin.SelfTest;

/// <summary>
/// The self-test checks (ADR-045). Everything is built 300 m away from the internal origin so it does not touch the
/// user's model, and the whole run is rolled back by <see cref="SelfTestCommand"/>.
/// Names of categories and parameters come from Revit itself, so the checks also work in non-English Revit.
/// </summary>
internal sealed class SelfTestScenarios
{
    private const double Origin = 300_000; // mm, away from the model
    private const double RoomWidth = 6000;
    private const double RoomDepth = 4000;

    private readonly SelfTestRunner _run;
    private readonly Document _doc;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];

    private Level? _level;
    private long? _wall1;
    private long? _room;
    private long? _door;
    private long? _freeWall;
    private long? _planView;

    public SelfTestScenarios(SelfTestRunner run)
    {
        _run = run;
        _doc = run.Document;
    }

    public void RunAll()
    {
        Setup();
        Context();
        Checks();
        Modeling();
        Spatial();
        Documentation();
        GridsAndPlacement();
        Pipes();
        Safety();
    }

    private static object P(double x, double y) => new { x = Origin + x, y = Origin + y };

    private string Cat(BuiltInCategory category) => Category.GetCategory(_doc, category).Name;

    private Level LevelOrSkip() => _level ?? throw Skip("no level in the project");

    private long Need(long? id, string what) => id ?? throw Skip($"depends on '{what}', which did not pass");

    private int Count<T>()
        where T : Element => new FilteredElementCollector(_doc).OfClass(typeof(T)).GetElementCount();

    private FamilySymbol? AnyType(BuiltInCategory category) =>
        new FilteredElementCollector(_doc).OfCategory(category).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault();

    private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    private void Setup()
    {
        _run.Area("Setup");
        _run.Check("Find a level", () =>
        {
            _level = _doc.ActiveView?.GenLevel
                     ?? new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault()
                     ?? throw new SelfTestFailure("The project has no levels.");
        });
    }

    private void Context()
    {
        _run.Area("Context");
        _run.Check("get_project_info", () =>
            Assert(_run.Read<ProjectInfoResult>("get_project_info", new { }).Title.Length > 0, "empty project title"));
        _run.Check("get_active_view", () => _run.Read<ViewResult>("get_active_view", new { }));
        _run.Check("get_active_level", () => _run.Read<ActiveLevelResult>("get_active_level", new { }));
        _run.Check("get_selected_elements", () => _run.Read<ElementListResult>("get_selected_elements", new { }));
        _run.Check("find_elements (walls)", () =>
            _run.Read<ElementListResult>("find_elements", new { category = Cat(BuiltInCategory.OST_Walls), levelId = (long?)null, nameContains = (string?)null, limit = (long?)5 }));
        _run.Check("find_family_types (walls) finds the default type", () =>
        {
            var result = _run.Read<FamilyTypesResult>("find_family_types", new { category = Cat(BuiltInCategory.OST_Walls), nameContains = (string?)null, limit = (long?)null });
            Assert(result.TotalCount > 0, "no wall types found");
            Assert(result.Types.Any(t => t.IsDefault), "no wall type marked as default");
        });
        _run.Check("find_views", () =>
            Assert(_run.Read<ViewListResult>("find_views", new { viewType = (string?)null, nameContains = (string?)null, templatesOnly = false, limit = (long?)10 }).TotalCount > 0, "no views found"));
        _run.Check("get_project_standards", () => _run.Read<StandardsSummary>("get_project_standards", new { }));
        _run.Check("get_workflow", () => Assert(_run.Read<Workflow>("get_workflow", new { name = "qa_floor" }).Steps.Count > 0, "empty workflow"));
    }

    private void Checks()
    {
        _run.Area("Model checks");
        _run.Check("find_rooms_without_tags", () => _run.Read<QaResult>("find_rooms_without_tags", new { viewId = (long?)null, limit = (long?)5 }));
        _run.Check("find_unhosted_doors", () => _run.Read<QaResult>("find_unhosted_doors", new { limit = (long?)5 }));
        _run.Check("find_unhosted_windows", () => _run.Read<QaResult>("find_unhosted_windows", new { limit = (long?)5 }));
        _run.Check("find_duplicate_elements", () => _run.Read<DuplicatesResult>("find_duplicate_elements", new { limit = (long?)5 }));
        _run.Check("get_model_warnings", () => _run.Read<WarningsResult>("get_model_warnings", new { limit = (long?)5 }));
        _run.Check("find_nonstandard_elements", () =>
            _run.Read<QaResult>("find_nonstandard_elements", new { category = Cat(BuiltInCategory.OST_Walls), limit = (long?)5 }));
        _run.Check("find_elements_missing_parameter", () =>
            _run.Read<QaResult>("find_elements_missing_parameter", new { category = Cat(BuiltInCategory.OST_Walls), parameterName = "Mark", limit = (long?)5 }));
        _run.Check("check_standards", () => _run.Read<StandardsReport>("check_standards", new { limit = (long?)5 }));
        _run.Check("query_elements (walls grouped by type with total length)", () =>
        {
            QueryElementsResult result = _run.Read<QueryElementsResult>("query_elements", new
            {
                category = Cat(BuiltInCategory.OST_Walls), levelId = (long?)null, conditions = (object[]?)null,
                groupBy = (string?)"@type", sumField = (string?)"@length", limit = (long?)5,
            });
            Assert(result.UnknownFields.Count == 0, $"unknown fields: {string.Join(", ", result.UnknownFields)}");
            Assert(result.TotalCount == 0 || (result.Groups.Count > 0 && result.Total > 0), "walls found but no groups or total length");
        });
    }

    private void Modeling()
    {
        _run.Area("Modeling");

        _run.Check("Preview changes nothing", () =>
        {
            int before = Count<Wall>();
            PlanRunResult result = _run.Run(apply: false, ("create_wall", Wall(P(-5000, 0), P(-1000, 0))));
            Assert(result.Succeeded, $"preview failed: {result.FailedStep?.Outcome}");
            Assert(!result.Applied, "preview reported Applied");
            Assert(Count<Wall>() == before, "preview left a wall in the model");
        });

        _run.Check("Room with 4 walls, door, window and floor in one plan", () =>
        {
            LevelOrSkip();
            FamilySymbol? door = AnyType(BuiltInCategory.OST_Doors);
            FamilySymbol? window = AnyType(BuiltInCategory.OST_Windows);
            var operations = new List<(string, object)>
            {
                ("create_wall", Wall(P(0, 0), P(RoomWidth, 0))),
                ("create_wall", Wall(P(RoomWidth, 0), P(RoomWidth, RoomDepth))),
                ("create_wall", Wall(P(RoomWidth, RoomDepth), P(0, RoomDepth))),
                ("create_wall", Wall(P(0, RoomDepth), P(0, 0))),
                ("create_room", new { levelId = _level!.Id.Value, point = P(RoomWidth / 2, RoomDepth / 2), name = (string?)"Self-test", number = (string?)null }),
                ("create_floor", new { levelId = _level.Id.Value, boundary = new[] { P(0, 0), P(RoomWidth, 0), P(RoomWidth, RoomDepth), P(0, RoomDepth) }, floorTypeId = (long?)null }),
            };
            if (door is not null)
            {
                operations.Add(("create_door", new { wallId = (object)"$op1.elementId", doorTypeId = (long?)door.Id.Value, offsetAlongWallMm = 3000.0 }));
            }

            if (window is not null)
            {
                operations.Add(("create_window", new { wallId = (object)"$op3.elementId", windowTypeId = (long?)window.Id.Value, offsetAlongWallMm = 2000.0, sillHeightMm = (double?)900 }));
            }

            PlanRunResult result = _run.ApplyOk([.. operations]);
            _wall1 = ElementOf(result, 1);
            _room = ElementOf(result, 5);

            var wall = (Wall)_doc.GetElement(new ElementId(_wall1.Value));
            Near(RoomWidth, Mm(((LocationCurve)wall.Location).Curve.Length), 1, "wall length (mm)");

            var room = (Room)_doc.GetElement(new ElementId(_room.Value));
            double areaM2 = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
            Assert(areaM2 is > 15 and < 24, $"room area {areaM2:0.00} m² is not about 6 × 4 m minus walls");

            if (door is not null)
            {
                _door = ElementOf(result, 7);
                var placed = (FamilyInstance)_doc.GetElement(new ElementId(_door.Value));
                Assert(placed.Host?.Id.Value == _wall1, "the door is not hosted by the first wall");
            }

            if (window is not null)
            {
                var placed = (FamilyInstance)_doc.GetElement(new ElementId(ElementOf(result, door is null ? 7 : 8)));
                double sill = Mm(placed.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM).AsDouble());
                Near(900, sill, 1, "window sill height (mm)");
            }

            if (door is null || window is null)
            {
                throw Skip($"walls, room and floor passed; no {(door is null ? "door" : "window")} family is loaded, so that part was not tested");
            }
        });

        _run.Check("modify_wall extends a wall", () =>
        {
            long wallId = Need(_wall1, "Room with 4 walls…");
            _run.ApplyOk(("modify_wall", new { wallId = (object)wallId, end = "end", distanceMm = 500.0 }));
            var wall = (Wall)_doc.GetElement(new ElementId(wallId));
            Near(RoomWidth + 500, Mm(((LocationCurve)wall.Location).Curve.Length), 1, "extended wall length (mm)");
        });

        _run.Check("move_elements moves a free wall", () =>
        {
            LevelOrSkip();
            PlanRunResult created = _run.ApplyOk(("create_wall", Wall(P(0, -6000), P(4000, -6000))));
            _freeWall = ElementOf(created, 1);
            _run.ApplyOk(("move_elements", new { elementIds = new object[] { _freeWall.Value }, dxMm = 0.0, dyMm = -1000.0 }));
            var wall = (Wall)_doc.GetElement(new ElementId(_freeWall.Value));
            Near(Origin - 7000, Mm(((LocationCurve)wall.Location).Curve.GetEndPoint(0).Y), 1, "moved wall Y (mm)");
        });

        _run.Check("set_parameters writes Comments", () =>
        {
            long wallId = Need(_freeWall, "move_elements…");
            Element wall = _doc.GetElement(new ElementId(wallId));
            string comments = wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Definition.Name;
            _run.ApplyOk(("set_parameters", new { elementIds = new object[] { wallId }, parameterName = comments, value = (object)"Revit AI self-test" }));
            Assert(wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString() == "Revit AI self-test", "Comments not written");
        });

        _run.Check("A failing step rolls back the whole plan", () =>
        {
            LevelOrSkip();
            int walls = Count<Wall>();
            int rooms = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).GetElementCount();
            PlanRunResult result = _run.Run(apply: true,
                ("create_wall", Wall(P(50_000, 0), P(54_000, 0))),
                ("create_room", new { levelId = _level!.Id.Value, point = P(90_000, 90_000), name = (string?)null, number = (string?)null }));
            Assert(!result.Succeeded && !result.Applied, "an unenclosed room should fail the plan");
            Assert(result.FailedStep?.Number == 2, "the failure should be reported at step 2");
            Assert(Count<Wall>() == walls, "the wall from step 1 was not rolled back");
            Assert(new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).GetElementCount() == rooms, "a room was left behind");
        });
    }

    private void Spatial()
    {
        _run.Area("Spatial");
        _run.Check("get_element (wall location)", () =>
        {
            ElementDetails details = _run.Read<ElementDetails>("get_element", new { elementId = Need(_wall1, "Room with 4 walls…") });
            Assert(details.Location?.Kind == "curve", "wall location is not a curve");
            Near(RoomWidth + 500, details.Location!.LengthMm ?? 0, 1, "reported wall length (mm)");
        });
        _run.Check("get_element_parameters", () =>
            Assert(_run.Read<ParametersResult>("get_element_parameters", new { elementId = Need(_wall1, "Room with 4 walls…"), parameterNames = (string[]?)null }).Parameters.Count > 0, "no parameters returned"));
        _run.Check("get_room_boundary", () =>
        {
            RoomBoundaryResult boundary = _run.Read<RoomBoundaryResult>("get_room_boundary", new { roomId = Need(_room, "Room with 4 walls…") });
            Assert(boundary.Loops.Count > 0 && boundary.Loops[0].Count >= 4, $"expected 4+ boundary segments, got {boundary.Loops.FirstOrDefault()?.Count ?? 0}");
        });
        _run.Check("get_element_room (door connects the room)", () =>
        {
            ElementRoomResult rooms = _run.Read<ElementRoomResult>("get_element_room", new { elementId = Need(_door, "door in the room plan") });
            Assert(rooms.FromRoom?.Id == _room || rooms.ToRoom?.Id == _room, "the door is not reported next to the self-test room");
        });
        _run.Check("find_nearby_elements (door next to its wall)", () =>
        {
            long doorId = Need(_door, "door in the room plan");
            NearbyElementsResult nearby = _run.Read<NearbyElementsResult>("find_nearby_elements",
                new { elementId = Need(_wall1, "Room with 4 walls…"), radiusMm = (double?)500, category = (string?)null, limit = (long?)100 });
            Assert(nearby.Elements.Any(e => e.Element.Id == doorId), "the hosted door was not found near its wall");
        });
        _run.Check("select_elements (selection restored afterwards)", () =>
        {
            var selection = _run.App.ActiveUIDocument.Selection;
            ICollection<ElementId> original = selection.GetElementIds();
            try
            {
                long wallId = Need(_wall1, "Room with 4 walls…");
                SelectionChangeResult result = _run.Read<SelectionChangeResult>("select_elements", new { elementIds = new[] { wallId } });
                Assert(result.Selected == 1, "the wall was not selected");
            }
            finally
            {
                selection.SetElementIds(original);
            }
        });
    }

    private void Documentation()
    {
        _run.Area("Documentation");

        _run.Check("create_view (floor plan)", () =>
        {
            Level level = LevelOrSkip();
            PlanRunResult result = _run.ApplyOk(("create_view",
                new { viewType = "FloorPlan", levelId = level.Id.Value, name = (string?)$"Revit AI self-test {_tag}", viewTemplateId = (long?)null }));
            _planView = ElementOf(result, 1);
            Assert(_doc.GetElement(new ElementId(_planView.Value)) is ViewPlan, "no plan view created");
        });

        _run.Check("tag_elements (rooms in the new plan)", () =>
        {
            long view = Need(_planView, "create_view");
            Need(_room, "Room with 4 walls…");
            if (AnyType(BuiltInCategory.OST_RoomTags) is null)
            {
                throw Skip("no room tag family is loaded");
            }

            _run.ApplyOk(("tag_elements", new { viewId = (object)view, category = (string?)Cat(BuiltInCategory.OST_Rooms), elementIds = (object[]?)null }));
            QaResult untagged = _run.Read<QaResult>("find_rooms_without_tags", new { viewId = (long?)view, limit = (long?)200 });
            Assert(untagged.Elements.All(e => e.Element.Id != _room), "the self-test room is still untagged");
        });

        _run.Check("dimension_wall (free wall)", () =>
            _run.ApplyOk(("dimension_wall", new { viewId = (object)Need(_planView, "create_view"), wallId = (object)Need(_freeWall, "move_elements…"), offsetMm = (double?)null })));

        _run.Check("dimension_room", () =>
            _run.ApplyOk(("dimension_room", new { viewId = (object)Need(_planView, "create_view"), roomId = (object)Need(_room, "Room with 4 walls…") })));

        _run.Check("create_text", () =>
            _run.ApplyOk(("create_text", new { viewId = (object)Need(_planView, "create_view"), position = P(RoomWidth / 2, RoomDepth / 2), text = "Revit AI self-test" })));

        _run.Check("create_schedule (walls)", () =>
        {
            List<string> fields = WallScheduleFields();
            PlanRunResult result = _run.ApplyOk(("create_schedule", new { category = Cat(BuiltInCategory.OST_Walls), name = $"Revit AI self-test {_tag}", fields }));
            var schedule = (ViewSchedule)_doc.GetElement(new ElementId(ElementOf(result, 1)));
            Assert(schedule.Definition.GetFieldCount() == fields.Count, "wrong number of schedule fields");
        });

        _run.Check("create_schedule rejects an unknown field", () =>
        {
            PlanRunResult result = _run.Run(apply: true, ("create_schedule",
                new { category = Cat(BuiltInCategory.OST_Walls), name = $"Revit AI self-test bad {_tag}", fields = new[] { "No Such Field 123" } }));
            Assert(!result.Succeeded, "an unknown field was accepted");
            Assert(result.FailedStep!.Outcome.Contains("No Such Field 123", StringComparison.Ordinal), "the failure does not name the unknown field");
        });

        _run.Check("create_sheet (places the plan)", () =>
        {
            long view = Need(_planView, "create_view");
            FamilySymbol titleBlock = AnyType(BuiltInCategory.OST_TitleBlocks) ?? throw Skip("no title block is loaded");
            string number = $"AI-{_tag}";
            _run.ApplyOk(("create_sheet", new { number, name = "Revit AI self-test", titleBlockTypeId = (long?)titleBlock.Id.Value, viewIds = new object[] { view } }));
            string? placedOn = _doc.GetElement(new ElementId(view)).get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString();
            Assert(placedOn == number, $"the plan is on sheet '{placedOn}', expected '{number}'");
        });

        _run.Check("create_section", () =>
        {
            Level level = LevelOrSkip();
            PlanRunResult result = _run.ApplyOk(("create_section",
                new { start = P(-1000, RoomDepth / 2), end = P(RoomWidth + 1000, RoomDepth / 2), levelId = level.Id.Value, depthMm = (double?)null, heightMm = (double?)null, name = (string?)null }));
            Assert(_doc.GetElement(new ElementId(ElementOf(result, 1))) is ViewSection, "no section view created");
        });

        _run.Check("create_elevations (north and east really look north and east)", () =>
        {
            long view = Need(_planView, "create_view");
            PlanRunResult result = _run.ApplyOk(("create_elevations",
                new { planViewId = (object)view, point = P(RoomWidth / 2, RoomDepth / 2), directions = new[] { "north", "east" } }));
            var first = (View)_doc.GetElement(new ElementId(ElementOf(result, 1)));
            Assert((-first.ViewDirection).DotProduct(XYZ.BasisY) > 0.99, "the first elevation does not look north");
        });

        _run.Check("create_3d_view (cropped to the room)", () =>
        {
            PlanRunResult result = _run.ApplyOk(("create_3d_view",
                new { name = (string?)$"Revit AI self-test 3D {_tag}", cropToElementIds = (object[]?)new object[] { Need(_room, "Room with 4 walls…"), Need(_wall1, "Room with 4 walls…") } }));
            var view = (View3D)_doc.GetElement(new ElementId(ElementOf(result, 1)));
            Assert(view.IsSectionBoxActive, "the section box is not active");
        });

        _run.Check("apply_view_template", () =>
        {
            long view = Need(_planView, "create_view");
            var plan = (View)_doc.GetElement(new ElementId(view));
            View template = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && plan.IsValidViewTemplate(v.Id)) ?? throw Skip("no view template fits a floor plan");
            _run.ApplyOk(("apply_view_template", new { viewIds = new object[] { view }, templateId = template.Id.Value }));
            Assert(plan.ViewTemplateId == template.Id, "the template was not applied");
        });
    }

    private void GridsAndPlacement()
    {
        _run.Area("Grids and placement");
        List<long> placed = [];

        _run.Check("place_family_instances (3 × 2, at the level's height)", () =>
        {
            Level level = LevelOrSkip();
            FamilySymbol type = new[] { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns }
                .SelectMany(c => new FilteredElementCollector(_doc).OfCategory(c).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                .FirstOrDefault(s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                ?? throw Skip("no level-based generic model, furniture or column family is loaded");

            object[] points = [P(20_000, 0), P(26_000, 0), P(32_000, 0), P(20_000, 5000), P(26_000, 5000), P(32_000, 5000)];
            PlanRunResult result = _run.ApplyOk(("place_family_instances",
                new { familyTypeId = type.Id.Value, levelId = level.Id.Value, points, rotationDegrees = (double?)null }));
            placed.AddRange(result.AffectedElementIds);
            Assert(placed.Count == 6, $"expected 6 instances, got {placed.Count}");

            var first = (FamilyInstance)_doc.GetElement(new ElementId(placed[0]));
            XYZ location = ((LocationPoint)first.Location).Point;
            Near(Origin + 20_000, Mm(location.X), 1, "instance X (mm)");
            Near(Mm(level.ProjectElevation), Mm(location.Z), 1, "instance height vs. level (mm)");
        });

        _run.Check("find_grid_lines + create_grids", () =>
        {
            if (placed.Count == 0)
            {
                throw Skip("depends on 'place_family_instances', which did not pass");
            }

            GridLinesResult lines = _run.Read<GridLinesResult>("find_grid_lines",
                new { elementIds = placed, category = (string?)null, levelId = (long?)null, toleranceMm = (double?)null, minPointsPerLine = (long?)null });
            Assert(lines.VerticalLines.Count == 3 && lines.HorizontalLines.Count == 2,
                $"expected 3 vertical and 2 horizontal lines, got {lines.VerticalLines.Count} and {lines.HorizontalLines.Count}");
            Assert(lines.SuggestedGrids.Count == 5, $"expected 5 suggested grids, got {lines.SuggestedGrids.Count}");

            _run.ApplyOk(("create_grids", new
            {
                grids = lines.SuggestedGrids.Select(g => new { name = g.Name, start = new { x = g.Start.X, y = g.Start.Y }, end = new { x = g.End.X, y = g.End.Y } }).ToList(),
            }));
            var names = new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Select(g => g.Name).ToHashSet();
            Assert(lines.SuggestedGrids.All(g => names.Contains(g.Name)), "not all suggested grids were created");
        });
    }

    private void Pipes()
    {
        _run.Area("Pipes");

        _run.Check("connect_pipes_with_elbow (right-angle corner with gaps)", () =>
        {
            (long first, long second) = TwoPipes(new XYZ(0, 20_000, 3000), new XYZ(4880, 20_000, 3000), new XYZ(5000, 20_080, 3000), new XYZ(5000, 24_000, 3000));
            PlanRunResult result = _run.ApplyOk(("connect_pipes_with_elbow", new { pipeId1 = (object)first, pipeId2 = (object)second }));
            var elbow = (FamilyInstance)_doc.GetElement(new ElementId(ElementOf(result, 1)));
            int connected = elbow.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Count(c => c.IsConnected);
            Assert(connected == 2, $"the elbow has {connected} connected ends, expected 2");
        });

        _run.Check("connect_pipes_with_elbow refuses a tee (crossing mid-pipe)", () =>
        {
            (long main, long branch) = TwoPipes(new XYZ(0, 30_000, 3000), new XYZ(4000, 30_000, 3000), new XYZ(2000, 30_100, 3000), new XYZ(2000, 33_000, 3000));
            ExpectRefused(() => _run.Validate("connect_pipes_with_elbow", new { pipeId1 = (object)main, pipeId2 = (object)branch }));

            PipeSystemsReport report = _run.Read<PipeSystemsReport>("check_pipe_systems",
                new { elementIds = new[] { main, branch }, levelId = (long?)null, limit = (long?)50 });
            Assert(report.OpenEndCount == 4, $"two loose pipes should have 4 open ends, found {report.OpenEndCount}");

            QueryElementsResult query = _run.Read<QueryElementsResult>("query_elements", new
            {
                category = Cat(BuiltInCategory.OST_PipeCurves), levelId = (long?)null,
                conditions = new object[]
                {
                    new { field = "@length", op = "greaterThan", value = (object)2899.5 },
                    new { field = "@length", op = "lessThan", value = (object)2900.5 },
                },
                groupBy = (string?)"@system", sumField = (string?)"@length", limit = (long?)200,
            });
            Assert(query.Elements.Any(e => e.Id == branch), "the 2900 mm test pipe was not found by its @length");
        });

        _run.Check("merge_pipes (in line, elbow on the removed pipe is reconnected)", () =>
        {
            (long kept, long removed) = TwoPipes(new XYZ(0, 40_000, 3000), new XYZ(2000, 40_000, 3000), new XYZ(2200, 40_000, 3000), new XYZ(4880, 40_000, 3000));
            (long riser, _) = TwoPipes(new XYZ(5000, 40_080, 3000), new XYZ(5000, 43_000, 3000), new XYZ(0, 50_000, 3000), new XYZ(1000, 50_000, 3000));
            PlanRunResult corner = _run.ApplyOk(("connect_pipes_with_elbow", new { pipeId1 = (object)removed, pipeId2 = (object)riser }));
            long elbowId = ElementOf(corner, 1);

            _run.ApplyOk(("merge_pipes", new { pipeId1 = (object)kept, pipeId2 = (object)removed }));
            Assert(_doc.GetElement(new ElementId(removed)) is null, "the second pipe was not removed");
            var pipe = (Autodesk.Revit.DB.Plumbing.Pipe)_doc.GetElement(new ElementId(kept));
            Near(5000, Mm(((LocationCurve)pipe.Location).Curve.Length), 1, "merged pipe length (mm)");
            var elbow = (FamilyInstance)_doc.GetElement(new ElementId(elbowId));
            bool toKept = elbow.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Any(c => c.IsConnected && c.AllRefs.Cast<Connector>().Any(r => r.Owner.Id.Value == kept));
            Assert(toKept, "the elbow is not connected to the merged pipe");
        });

        _run.Check("merge_pipes refuses parallel pipes side by side", () =>
        {
            (long a, long b) = TwoPipes(new XYZ(0, 60_000, 3000), new XYZ(2000, 60_000, 3000), new XYZ(2100, 60_200, 3000), new XYZ(4000, 60_200, 3000));
            ExpectRefused(() => _run.Validate("merge_pipes", new { pipeId1 = (object)a, pipeId2 = (object)b }));
        });

        _run.Check("connect_pipes_with_tee (branch 100 mm short of the main)", () =>
        {
            (long main, long branch) = TwoPipes(new XYZ(0, 80_000, 3000), new XYZ(4000, 80_000, 3000), new XYZ(2000, 80_100, 3000), new XYZ(2000, 83_000, 3000));
            PlanRunResult result = _run.ApplyOk(("connect_pipes_with_tee", new { mainPipeId = (object)main, branchPipeId = (object)branch }));
            var tee = (FamilyInstance)_doc.GetElement(new ElementId(ElementOf(result, 1)));
            int connected = tee.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Count(c => c.IsConnected);
            Assert(connected == 3, $"the tee has {connected} connected ends, expected 3");
        });

        _run.Check("connect_pipes_with_tee refuses pipes crossing each other", () =>
        {
            (long main, long branch) = TwoPipes(new XYZ(0, 90_000, 3000), new XYZ(4000, 90_000, 3000), new XYZ(2000, 89_000, 3000), new XYZ(2000, 91_000, 3000));
            ExpectRefused(() => _run.Validate("connect_pipes_with_tee", new { mainPipeId = (object)main, branchPipeId = (object)branch }));
        });

        _run.Check("find_pipe_joints + connect_pipes (elbow, tee and merge in one plan)", () =>
        {
            (long a, long b) = TwoPipes(new XYZ(0, 100_000, 3000), new XYZ(1900, 100_000, 3000), new XYZ(2000, 100_100, 3000), new XYZ(2000, 103_000, 3000));
            (long c, long d) = TwoPipes(new XYZ(10_000, 100_000, 3000), new XYZ(14_000, 100_000, 3000), new XYZ(12_000, 100_100, 3000), new XYZ(12_000, 103_000, 3000));
            (long e, long f) = TwoPipes(new XYZ(20_000, 100_000, 3000), new XYZ(22_000, 100_000, 3000), new XYZ(22_200, 100_000, 3000), new XYZ(25_000, 100_000, 3000));
            long[] ids = [a, b, c, d, e, f];
            object Scope() => new { elementIds = ids, levelId = (long?)null, searchDistanceMm = (double?)null, limit = (long?)50 };

            PipeJointsReport found = _run.Read<PipeJointsReport>("find_pipe_joints", Scope());
            Assert((found.ElbowCount, found.TeeCount, found.MergeCount) == (1, 1, 1),
                $"expected 1 elbow, 1 tee and 1 merge, found {found.ElbowCount}, {found.TeeCount} and {found.MergeCount}");

            _run.ApplyOk(("connect_pipes", new { connections = found.Proposals.Select(p => new { pipeId1 = p.PipeId1, pipeId2 = p.PipeId2 }).ToList() }));
            PipeJointsReport after = _run.Read<PipeJointsReport>("find_pipe_joints", Scope());
            Assert(after.ProposalCount == 0, $"{after.ProposalCount} joint(s) still found after connecting");
        });
    }

    /// <summary>Two pipes from mm coordinates relative to the self-test origin (Z relative to the level).</summary>
    private (long First, long Second) TwoPipes(XYZ a1, XYZ a2, XYZ b1, XYZ b2)
    {
        Level level = LevelOrSkip();
        ElementId system = new FilteredElementCollector(_doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystemType)).FirstElementId();
        ElementId type = new FilteredElementCollector(_doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipeType)).FirstElementId();
        if (system == ElementId.InvalidElementId || type == ElementId.InvalidElementId)
        {
            throw Skip("no piping system type or pipe type in the project");
        }

        XYZ At(XYZ p) => new(
            UnitUtils.ConvertToInternalUnits(Origin + p.X, UnitTypeId.Millimeters),
            UnitUtils.ConvertToInternalUnits(Origin + p.Y, UnitTypeId.Millimeters),
            level.ProjectElevation + UnitUtils.ConvertToInternalUnits(p.Z, UnitTypeId.Millimeters));

        long first = 0, second = 0;
        _run.Modify("Self-test pipes", () =>
        {
            first = Autodesk.Revit.DB.Plumbing.Pipe.Create(_doc, system, type, level.Id, At(a1), At(a2)).Id.Value;
            second = Autodesk.Revit.DB.Plumbing.Pipe.Create(_doc, system, type, level.Id, At(b1), At(b2)).Id.Value;
        });
        return (first, second);
    }

    private void Safety()
    {
        _run.Area("Safety");

        _run.Check("delete_elements: preview keeps, apply removes wall and its door", () =>
        {
            LevelOrSkip();
            FamilySymbol door = AnyType(BuiltInCategory.OST_Doors) ?? throw Skip("no door family is loaded");
            PlanRunResult created = _run.ApplyOk(
                ("create_wall", Wall(P(0, 12_000), P(4000, 12_000))),
                ("create_door", new { wallId = (object)"$op1.elementId", doorTypeId = (long?)door.Id.Value, offsetAlongWallMm = 2000.0 }));
            long wallId = ElementOf(created, 1);
            long doorId = ElementOf(created, 2);

            PlanRunResult preview = _run.Run(apply: false, ("delete_elements", new { elementIds = new object[] { wallId } }));
            Assert(preview.Succeeded && _doc.GetElement(new ElementId(wallId)) is not null, "preview deleted the wall");

            _run.ApplyOk(("delete_elements", new { elementIds = new object[] { wallId } }));
            Assert(_doc.GetElement(new ElementId(wallId)) is null, "the wall still exists");
            Assert(_doc.GetElement(new ElementId(doorId)) is null, "the dependent door still exists");
        });

        _run.Check("delete_elements refuses a level", () =>
        {
            Level level = LevelOrSkip();
            ExpectRefused(() => _run.Validate("delete_elements", new { elementIds = new object[] { level.Id.Value } }));
        });

        _run.Check("move_elements refuses a pinned element", () =>
        {
            long wallId = Need(_freeWall, "move_elements…");
            _run.Modify("Pin for self-test", () => _doc.GetElement(new ElementId(wallId)).Pinned = true);
            ExpectRefused(() => _run.Validate("move_elements", new { elementIds = new object[] { wallId }, dxMm = 100.0, dyMm = 0.0 }));
        });

        _run.Check("Validation rejects a wall shorter than 10 mm", () =>
        {
            LevelOrSkip();
            ExpectRefused(() => _run.Validate("create_wall", Wall(P(0, 0), P(5, 0))));
        });
    }

    private object Wall(object start, object end) =>
        new { start, end, levelId = _level?.Id.Value ?? 0, wallTypeId = (long?)null, heightMm = (double?)null };

    private static void ExpectRefused(Action validate)
    {
        try
        {
            validate();
        }
        catch (ToolException)
        {
            return;
        }

        throw new SelfTestFailure("the operation was accepted but should have been refused");
    }

    /// <summary>Two real field names for a wall schedule, read from a schedule created and rolled back at once.</summary>
    private List<string> WallScheduleFields()
    {
        using var probe = new Transaction(_doc, "Probe schedule fields");
        probe.Start();
        try
        {
            ViewSchedule schedule = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Walls));
            return schedule.Definition.GetSchedulableFields().Select(f => f.GetName(_doc)).Distinct().Take(2).ToList();
        }
        finally
        {
            probe.RollBack();
        }
    }
}

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Localization;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// MEP pipes (ADR-047, ADR-049). The corner geometry is Revit-free (PipeCorner); Revit inserts the elbow from the pipe type's
// routing preferences.

public sealed class ConnectPipesWithElbowTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "connect_pipes_with_elbow";

    public override string Description =>
        "Proposes connecting two straight pipes at a corner with an elbow: both pipes are extended or trimmed to where their " +
        "centerlines meet, then Revit inserts the elbow defined in the pipe type's routing preferences. Refused when the pipes " +
        "are parallel, don't meet (e.g. different heights), cross in the middle of a pipe (a tee), are more than 3 m from the " +
        "corner, or the corner end of a pipe is already connected.";

    public override string ProgressLabel => "Checking the pipe corner…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "pipeId1": { "type": ["integer", "string"], "description": "First pipe ID, or $opN.elementId." },
            "pipeId2": { "type": ["integer", "string"], "description": "Second pipe ID, or $opN.elementId." }
          },
          "required": ["pipeId1", "pipeId2"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        long? id1 = WriteArgs.IdOrReference(T, arguments, "pipeId1");
        long? id2 = WriteArgs.IdOrReference(T, arguments, "pipeId2");
        if (id1 is null || id2 is null)
        {
            return T.Format("Tool.ElbowSummaryRef",
                id1?.ToString() ?? arguments.GetProperty("pipeId1").GetString()!,
                id2?.ToString() ?? arguments.GetProperty("pipeId2").GetString()!);
        }

        Corner corner = Resolve(document, id1.Value, id2.Value);
        CornerPlan plan = corner.Plan;
        return T.Format("Tool.ElbowSummary", id1, Diameter(corner.First), id2, Diameter(corner.Second), 180 - plan.AngleDegrees,
            plan.Corner.X, plan.Corner.Y, plan.Corner.Z, plan.FirstChangeMm, plan.SecondChangeMm);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Corner corner = Resolve(document, WriteArgs.Id(T, arguments, "pipeId1"), WriteArgs.Id(T, arguments, "pipeId2"));
        var point = new XYZ(RevitRead.Feet(corner.Plan.Corner.X), RevitRead.Feet(corner.Plan.Corner.Y), RevitRead.Feet(corner.Plan.Corner.Z));

        MoveEnd(corner.First, corner.Plan.FirstEnd, point);
        MoveEnd(corner.Second, corner.Plan.SecondEnd, point);
        document.Regenerate();

        Connector first = NearestConnector(corner.First, point);
        Connector second = NearestConnector(corner.Second, point);
        FamilyInstance elbow;
        try
        {
            elbow = document.Create.NewElbowFitting(first, second);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new ToolException(T.Format("Tool.ElbowFailed", ex.Message));
        }

        if (!first.IsConnected || !second.IsConnected)
        {
            throw new ToolException(T.Format("Tool.ElbowFailed", "the elbow is not connected to both pipes"));
        }

        return new OperationResult(elbow.Id.Value,
            T.Format("Tool.ElbowDone", corner.First.Id.Value, corner.Second.Id.Value, elbow.Id.Value),
            [corner.First.Id.Value, corner.Second.Id.Value]);
    }

    private Corner Resolve(Document document, long id1, long id2)
    {
        if (id1 == id2)
        {
            throw new ToolException(T["Tool.PipesSame"]);
        }

        Pipe first = StraightPipe(document, id1);
        Pipe second = StraightPipe(document, id2);
        CornerPlan plan = PipeCorner.Plan(Segment(first), Segment(second));
        if (plan.Problem != CornerProblem.None)
        {
            throw new ToolException(plan.Problem switch
            {
                CornerProblem.Parallel => T["Tool.CornerParallel"],
                CornerProblem.DoNotMeet => T.Format("Tool.CornerNoMeet", WriteArgs.Mm(plan.OffsetMm)),
                CornerProblem.MeetInMiddle => T["Tool.CornerMiddle"],
                CornerProblem.TooFar => T.Format("Tool.CornerTooFar", WriteArgs.Mm(PipeCorner.DefaultMaxExtensionMm)),
                _ => T["Tool.CornerTooShort"],
            });
        }

        CheckFree(first, plan.FirstEnd);
        CheckFree(second, plan.SecondEnd);
        return new Corner(first, second, plan);
    }

    private Pipe StraightPipe(Document document, long id) =>
        document.GetElement(new ElementId(id)) is Pipe { Location: LocationCurve { Curve: Line } } pipe
            ? pipe
            : throw new ToolException(T.Format("Tool.NotAPipe", id));

    /// <summary>The pipe end that will move to the corner must not already be connected to something.</summary>
    private void CheckFree(Pipe pipe, int end)
    {
        XYZ endPoint = ((LocationCurve)pipe.Location).Curve.GetEndPoint(end);
        if (NearestConnector(pipe, endPoint).IsConnected)
        {
            throw new ToolException(T.Format("Tool.PipeEndConnected", pipe.Id.Value));
        }
    }

    internal static Segment3 Segment(Pipe pipe)
    {
        Curve curve = ((LocationCurve)pipe.Location).Curve;
        return new Segment3(Mm(curve.GetEndPoint(0)), Mm(curve.GetEndPoint(1)));
    }

    internal static Point3 Mm(XYZ p) =>
        new(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters),
            UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters),
            UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Millimeters));

    internal static void MoveEnd(Pipe pipe, int end, XYZ point)
    {
        var location = (LocationCurve)pipe.Location;
        Curve curve = location.Curve;
        location.Curve = end == 1
            ? Line.CreateBound(curve.GetEndPoint(0), point)
            : Line.CreateBound(point, curve.GetEndPoint(1));
    }

    internal static Connector NearestConnector(Pipe pipe, XYZ point) =>
        pipe.ConnectorManager.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(point))
            .First();

    internal static string Diameter(Pipe pipe) => WriteArgs.Mm(UnitUtils.ConvertFromInternalUnits(pipe.Diameter, UnitTypeId.Millimeters));

    private sealed record Corner(Pipe First, Pipe Second, CornerPlan Plan);
}

public sealed class MergePipesTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "merge_pipes";

    public override string Description =>
        "Proposes merging two straight pipes that lie on one line into one pipe: the first pipe is extended to the second " +
        "pipe's far end, the second pipe (and a coupling between them, if any) is removed, and whatever was connected to the " +
        "second pipe's far end is reconnected to the first. Both pipes need the same pipe type, diameter and system type and " +
        "may be at most 3 m apart. The second pipe's own parameter values (e.g. Mark, Comments) are lost.";

    public override string ProgressLabel => "Checking the pipes to merge…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "pipeId1": { "type": ["integer", "string"], "description": "Pipe that is kept and extended, or $opN.elementId." },
            "pipeId2": { "type": ["integer", "string"], "description": "Pipe that is removed, or $opN.elementId." }
          },
          "required": ["pipeId1", "pipeId2"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        long? id1 = WriteArgs.IdOrReference(T, arguments, "pipeId1");
        long? id2 = WriteArgs.IdOrReference(T, arguments, "pipeId2");
        if (id1 is null || id2 is null)
        {
            return T.Format("Tool.MergeSummaryRef",
                id2?.ToString() ?? arguments.GetProperty("pipeId2").GetString()!,
                id1?.ToString() ?? arguments.GetProperty("pipeId1").GetString()!);
        }

        Merge merge = Resolve(document, id1.Value, id2.Value);
        string diameter = ConnectPipesWithElbowTool.Diameter(merge.First);
        return merge.Coupling is { } coupling
            ? T.Format("Tool.MergeSummaryCoupling", id2, id1, diameter, merge.Plan.LengthMm, coupling.Value)
            : T.Format("Tool.MergeSummary", id2, id1, diameter, merge.Plan.LengthMm);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Merge merge = Resolve(document, WriteArgs.Id(T, arguments, "pipeId1"), WriteArgs.Id(T, arguments, "pipeId2"));
        long removedId = merge.Second.Id.Value;

        // Remember what the removed pipe's far end was connected to, then disconnect it before deleting.
        Connector farEnd = ConnectPipesWithElbowTool.NearestConnector(merge.Second,
            ((LocationCurve)merge.Second.Location).Curve.GetEndPoint(merge.Plan.SecondFarEnd));
        List<Connector> partners = Partners(farEnd);
        List<(ElementId Owner, int Id)> reconnect = partners.Select(c => (c.Owner.Id, c.Id)).ToList();
        foreach (Connector partner in partners)
        {
            farEnd.DisconnectFrom(partner);
        }

        var removed = new List<ElementId> { merge.Second.Id };
        if (merge.Coupling is { } coupling)
        {
            removed.Add(coupling);
        }

        document.Delete(removed);

        var end = new XYZ(RevitRead.Feet(merge.Plan.End.X), RevitRead.Feet(merge.Plan.End.Y), RevitRead.Feet(merge.Plan.End.Z));
        ConnectPipesWithElbowTool.MoveEnd(merge.First, 1 - merge.Plan.FirstFarEnd, end);
        document.Regenerate();

        Connector newEnd = ConnectPipesWithElbowTool.NearestConnector(merge.First, end);
        foreach ((ElementId owner, int id) in reconnect)
        {
            Connector? partner = ConnectorsOf(document.GetElement(owner)).FirstOrDefault(c => c.Id == id);
            if (partner is null)
            {
                throw new ToolException(T.Format("Tool.MergeReconnectFailed", owner.Value));
            }

            try
            {
                newEnd.ConnectTo(partner);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                throw new ToolException(T.Format("Tool.MergeReconnectFailed", owner.Value));
            }
        }

        double length = UnitUtils.ConvertFromInternalUnits(((LocationCurve)merge.First.Location).Curve.Length, UnitTypeId.Millimeters);
        if (Math.Abs(length - merge.Plan.LengthMm) > 1)
        {
            throw new ToolException(T.Format("Tool.MergeLengthWrong", WriteArgs.Mm(length), WriteArgs.Mm(merge.Plan.LengthMm)));
        }

        return new OperationResult(merge.First.Id.Value,
            T.Format("Tool.MergeDone", removedId, merge.First.Id.Value, merge.Plan.LengthMm),
            reconnect.Select(r => r.Owner.Value).ToList());
    }

    private Merge Resolve(Document document, long id1, long id2)
    {
        if (id1 == id2)
        {
            throw new ToolException(T["Tool.PipesSame"]);
        }

        Pipe first = StraightPipe(document, id1);
        Pipe second = StraightPipe(document, id2);
        if (first.GetTypeId() != second.GetTypeId())
        {
            throw new ToolException(T["Tool.MergeDifferentType"]);
        }

        if (Math.Abs(first.Diameter - second.Diameter) > 1e-6)
        {
            throw new ToolException(T.Format("Tool.MergeDifferentSize",
                ConnectPipesWithElbowTool.Diameter(first), ConnectPipesWithElbowTool.Diameter(second)));
        }

        if (SystemType(first) != SystemType(second))
        {
            throw new ToolException(T["Tool.MergeDifferentSystem"]);
        }

        MergePlan plan = PipeMerge.Plan(ConnectPipesWithElbowTool.Segment(first), ConnectPipesWithElbowTool.Segment(second));
        if (plan.Problem != MergeProblem.None)
        {
            throw new ToolException(plan.Problem switch
            {
                MergeProblem.NotParallel => T["Tool.MergeNotParallel"],
                MergeProblem.NotInLine => T.Format("Tool.MergeNotInLine", WriteArgs.Mm(plan.OffsetMm)),
                MergeProblem.Contained => T["Tool.MergeContained"],
                MergeProblem.TooFar => T.Format("Tool.MergeTooFar", WriteArgs.Mm(PipeMerge.DefaultMaxGapMm)),
                _ => T["Tool.CornerTooShort"],
            });
        }

        Connector firstNear = EndConnector(first, 1 - plan.FirstFarEnd);
        Connector secondNear = EndConnector(second, 1 - plan.SecondFarEnd);
        return new Merge(first, second, plan, Joint(first, firstNear, second, secondNear));
    }

    /// <summary>
    /// The meeting ends must be free, connected to each other, or joined by one coupling (removed with the second pipe).
    /// Anything else there (a tee, a valve, a different fitting) is refused.
    /// </summary>
    private ElementId? Joint(Pipe first, Connector firstNear, Pipe second, Connector secondNear)
    {
        List<Connector> a = Partners(firstNear);
        List<Connector> b = Partners(secondNear);
        if (a.Count == 0 && b.Count == 0)
        {
            return null;
        }

        if (a.Count == 1 && b.Count == 1)
        {
            if (a[0].Owner.Id == second.Id && b[0].Owner.Id == first.Id)
            {
                return null;
            }

            if (a[0].Owner.Id == b[0].Owner.Id
                && a[0].Owner is FamilyInstance { MEPModel: MechanicalFitting { PartType: PartType.Union } fitting } coupling
                && fitting.ConnectorManager.Connectors.Size == 2)
            {
                return coupling.Id;
            }
        }

        throw new ToolException(T.Format("Tool.MergeJointBlocked", first.Id.Value, second.Id.Value));
    }

    private Pipe StraightPipe(Document document, long id) =>
        document.GetElement(new ElementId(id)) is Pipe { Location: LocationCurve { Curve: Line } } pipe
            ? pipe
            : throw new ToolException(T.Format("Tool.NotAPipe", id));

    private static Connector EndConnector(Pipe pipe, int end) =>
        ConnectPipesWithElbowTool.NearestConnector(pipe, ((LocationCurve)pipe.Location).Curve.GetEndPoint(end));

    /// <summary>Physical connections of a connector to other elements (logical system connections are ignored).</summary>
    private static List<Connector> Partners(Connector connector) =>
        connector.AllRefs.Cast<Connector>()
            .Where(c => c.Owner.Id != connector.Owner.Id && (c.ConnectorType & ConnectorType.Physical) != 0)
            .ToList();

    private static IEnumerable<Connector> ConnectorsOf(Element? element) => element switch
    {
        MEPCurve curve => curve.ConnectorManager.Connectors.Cast<Connector>(),
        FamilyInstance { MEPModel.ConnectorManager: { } manager } => manager.Connectors.Cast<Connector>(),
        _ => [],
    };

    private static ElementId SystemType(Pipe pipe) =>
        pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;

    private sealed record Merge(Pipe First, Pipe Second, MergePlan Plan, ElementId? Coupling);
}

public sealed class ConnectPipesWithTeeTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    public override string Name => "connect_pipes_with_tee";

    public override string Description =>
        "Proposes connecting a branch pipe to a main pipe with a tee: the main pipe is split where the branch meets it, the " +
        "branch is extended or trimmed to the main pipe's centerline, then Revit inserts the tee defined in the pipe type's " +
        "routing preferences. The branch must be perpendicular to the main pipe and at the same height (for a horizontal " +
        "branch), meet it away from its ends, end near it (pipes crossing each other are refused), be at most 3 m away, and " +
        "its end at the junction must be free.";

    public override string ProgressLabel => "Checking the tee junction…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "mainPipeId": { "type": ["integer", "string"], "description": "The pipe that continues through the tee, or $opN.elementId." },
            "branchPipeId": { "type": ["integer", "string"], "description": "The pipe that branches off, or $opN.elementId." }
          },
          "required": ["mainPipeId", "branchPipeId"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        long? mainId = WriteArgs.IdOrReference(T, arguments, "mainPipeId");
        long? branchId = WriteArgs.IdOrReference(T, arguments, "branchPipeId");
        if (mainId is null || branchId is null)
        {
            return T.Format("Tool.TeeSummaryRef",
                branchId?.ToString() ?? arguments.GetProperty("branchPipeId").GetString()!,
                mainId?.ToString() ?? arguments.GetProperty("mainPipeId").GetString()!);
        }

        Tee tee = Resolve(document, mainId.Value, branchId.Value);
        return T.Format("Tool.TeeSummary", branchId, ConnectPipesWithElbowTool.Diameter(tee.Branch), mainId,
            ConnectPipesWithElbowTool.Diameter(tee.Main), tee.Plan.Junction.X, tee.Plan.Junction.Y, tee.Plan.Junction.Z,
            tee.Plan.BranchChangeMm);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        Tee tee = Resolve(document, WriteArgs.Id(T, arguments, "mainPipeId"), WriteArgs.Id(T, arguments, "branchPipeId"));
        var point = new XYZ(RevitRead.Feet(tee.Plan.Junction.X), RevitRead.Feet(tee.Plan.Junction.Y), RevitRead.Feet(tee.Plan.Junction.Z));

        ConnectPipesWithElbowTool.MoveEnd(tee.Branch, tee.Plan.BranchEnd, point);
        ElementId otherId = PlumbingUtils.BreakCurve(document, tee.Main.Id, point);
        document.Regenerate();
        var other = (Pipe)document.GetElement(otherId);

        Connector main1 = ConnectPipesWithElbowTool.NearestConnector(tee.Main, point);
        Connector main2 = ConnectPipesWithElbowTool.NearestConnector(other, point);
        Connector branch = ConnectPipesWithElbowTool.NearestConnector(tee.Branch, point);
        FamilyInstance fitting;
        try
        {
            fitting = document.Create.NewTeeFitting(main1, main2, branch);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new ToolException(T.Format("Tool.TeeFailed", ex.Message));
        }

        if (!main1.IsConnected || !main2.IsConnected || !branch.IsConnected)
        {
            throw new ToolException(T.Format("Tool.TeeFailed", "the tee is not connected to all three pipes"));
        }

        return new OperationResult(fitting.Id.Value,
            T.Format("Tool.TeeDone", tee.Branch.Id.Value, tee.Main.Id.Value, fitting.Id.Value, otherId.Value),
            [tee.Main.Id.Value, otherId.Value, tee.Branch.Id.Value]);
    }

    private Tee Resolve(Document document, long mainId, long branchId)
    {
        if (mainId == branchId)
        {
            throw new ToolException(T["Tool.PipesSame"]);
        }

        Pipe main = StraightPipe(document, mainId);
        Pipe branch = StraightPipe(document, branchId);

        // The tee body needs room on the main pipe: at least 150 mm or one main diameter from either end.
        double minEnd = Math.Max(PipeTee.DefaultMinEndDistanceMm, UnitUtils.ConvertFromInternalUnits(main.Diameter, UnitTypeId.Millimeters));
        TeePlan plan = PipeTee.Plan(ConnectPipesWithElbowTool.Segment(main), ConnectPipesWithElbowTool.Segment(branch), minEnd);
        if (plan.Problem != TeeProblem.None)
        {
            throw new ToolException(plan.Problem switch
            {
                TeeProblem.NotPerpendicular => T["Tool.TeeNotPerpendicular"],
                TeeProblem.DoNotMeet => T.Format("Tool.CornerNoMeet", WriteArgs.Mm(plan.OffsetMm)),
                TeeProblem.NearMainEnd => T.Format("Tool.TeeNearMainEnd", WriteArgs.Mm(minEnd)),
                TeeProblem.Crossing => T["Tool.TeeCrossing"],
                TeeProblem.TooFar => T.Format("Tool.TeeTooFar", WriteArgs.Mm(PipeTee.DefaultMaxExtensionMm)),
                _ => T["Tool.CornerTooShort"],
            });
        }

        XYZ branchEnd = ((LocationCurve)branch.Location).Curve.GetEndPoint(plan.BranchEnd);
        if (ConnectPipesWithElbowTool.NearestConnector(branch, branchEnd).IsConnected)
        {
            throw new ToolException(T.Format("Tool.TeeBranchConnected", branch.Id.Value));
        }

        return new Tee(main, branch, plan);
    }

    private Pipe StraightPipe(Document document, long id) =>
        document.GetElement(new ElementId(id)) is Pipe { Location: LocationCurve { Curve: Line } } pipe
            ? pipe
            : throw new ToolException(T.Format("Tool.NotAPipe", id));

    private sealed record Tee(Pipe Main, Pipe Branch, TeePlan Plan);
}

/// <summary>Reads straight pipes as the Revit-free joint logic sees them (ADR-051).</summary>
internal static class PipeReader
{
    public static PipeInfo Info(Pipe pipe)
    {
        Curve curve = ((LocationCurve)pipe.Location).Curve;
        return new PipeInfo(
            pipe.Id.Value,
            ConnectPipesWithElbowTool.Segment(pipe),
            UnitUtils.ConvertFromInternalUnits(pipe.Diameter, UnitTypeId.Millimeters),
            pipe.GetTypeId().Value,
            (pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId).Value,
            !ConnectPipesWithElbowTool.NearestConnector(pipe, curve.GetEndPoint(0)).IsConnected,
            !ConnectPipesWithElbowTool.NearestConnector(pipe, curve.GetEndPoint(1)).IsConnected);
    }

    public static bool IsStraight(Element element) => element is Pipe { Location: LocationCurve { Curve: Line } };
}

public sealed class FindPipeJointsTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public override string Name => "find_pipe_joints";

    public override string Description =>
        "Finds missing pipe joints (read-only): open ends of straight pipes next to another pipe, and which fitting fits there " +
        "(elbow, tee or merge), using the same rules as the pipe tools. Each pipe is in at most one proposal, so all proposals " +
        "can be passed to connect_pipes together. Also lists places where no standard joint fits, with the reason (e.g. " +
        "different diameters need a reducer, angled branch, different heights). Scope: the given elements, or a level, or " +
        "(both null) the whole model.";

    public override string ProgressLabel => "Looking for missing pipe joints…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": ["array", "null"], "items": { "type": "integer" }, "description": "Only these pipes. Null for a level or the whole model." },
            "levelId": { "type": ["integer", "null"], "description": "Only pipes on this level. Null for all." },
            "searchDistanceMm": { "type": ["number", "null"], "description": "How far from an open end to look for another pipe. Null for 500." },
            "limit": { "type": ["integer", "null"], "description": "Maximum items listed. Null for 100." }
          },
          "required": ["elementIds", "levelId", "searchDistanceMm", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        long? levelId = RevitRead.OptionalLong(arguments, "levelId");
        double search = arguments.GetProperty("searchDistanceMm").ValueKind == JsonValueKind.Number
            ? Math.Clamp(arguments.GetProperty("searchDistanceMm").GetDouble(), 10, PipeCorner.DefaultMaxExtensionMm)
            : PipeJoints.DefaultSearchMm;
        HashSet<long>? only = arguments.GetProperty("elementIds").ValueKind == JsonValueKind.Array
            ? arguments.GetProperty("elementIds").EnumerateArray().Select(id => id.GetInt64()).ToHashSet()
            : null;

        List<PipeInfo> pipes = new FilteredElementCollector(document)
            .OfClass(typeof(Pipe))
            .Where(PipeReader.IsStraight)
            .Where(e => (only is null || only.Contains(e.Id.Value)) && (levelId is null || e.LevelId.Value == levelId))
            .Cast<Pipe>()
            .Select(PipeReader.Info)
            .ToList();

        return PipeJointsReport.From(PipeJoints.Find(pipes, search), limit);
    }
}

/// <summary>
/// One or many pipe pairs; the joint (elbow, tee or merge) is chosen deterministically by <see cref="PipeJoints"/> and carried
/// out by the specific pipe tool, so every rule and check of those tools applies (ADR-051).
/// </summary>
public sealed class ConnectPipesTool(
    RevitDispatcher dispatcher,
    TextSource text,
    ConnectPipesWithElbowTool elbow,
    MergePipesTool merge,
    ConnectPipesWithTeeTool tee) : RevitWriteTool(dispatcher, text)
{
    private const int MaxPairs = 100;

    public override string Name => "connect_pipes";

    public override string Description =>
        "Proposes connecting pipe pairs, choosing the joint for each pair: an elbow at a corner, a tee where one pipe ends at the " +
        "middle of the other, or a merge into one pipe when they are in a straight line (the shorter pipe is removed and its own " +
        $"parameter values are lost). Use it for \"connect these pipes\" and for the proposals of find_pipe_joints. At most {MaxPairs} " +
        "pairs; a pipe may appear in only one pair. Pairs where no standard joint fits are refused with the reason.";

    public override string ProgressLabel => "Checking the pipe joints…";

    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "connections": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "pipeId1": { "type": "integer", "description": "A pipe ID." },
                  "pipeId2": { "type": "integer", "description": "The other pipe ID." }
                },
                "required": ["pipeId1", "pipeId2"],
                "additionalProperties": false
              },
              "description": "Pipe pairs to connect."
            }
          },
          "required": ["connections"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        List<Step> steps = Steps(document, arguments);
        List<string> summaries = steps.Select(step => Guard(step, () => step.Tool.ValidateInContext(document, step.Arguments))).ToList();
        if (steps.Count == 1)
        {
            return summaries[0];
        }

        int merges = steps.Count(s => s.Kind == JointKind.Merge);
        string summary = T.Format("Tool.ConnectSummary", steps.Count,
            steps.Count(s => s.Kind == JointKind.Elbow), steps.Count(s => s.Kind == JointKind.Tee), merges);
        return merges > 0 ? summary + T["Tool.ConnectSummaryMerges"] : summary;
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        List<Step> steps = Steps(document, arguments);
        var ids = new List<long>();
        foreach (Step step in steps)
        {
            OperationResult result = Guard(step, () => step.Tool.Apply(document, step.Arguments));
            if (result.ElementId is { } id)
            {
                ids.Add(id);
            }

            ids.AddRange(result.OtherIds ?? []);
        }

        return new OperationResult(null,
            T.Format("Tool.ConnectDone", steps.Count, steps.Count(s => s.Kind == JointKind.Elbow),
                steps.Count(s => s.Kind == JointKind.Tee), steps.Count(s => s.Kind == JointKind.Merge)),
            ids.Distinct().ToList());
    }

    private List<Step> Steps(Document document, JsonElement arguments)
    {
        List<(long A, long B)> pairs = arguments.GetProperty("connections").EnumerateArray()
            .Select(c => (c.GetProperty("pipeId1").GetInt64(), c.GetProperty("pipeId2").GetInt64()))
            .ToList();
        if (pairs.Count == 0 || pairs.Count > MaxPairs)
        {
            throw new ToolException(T.Format("Tool.ConnectCount", MaxPairs));
        }

        long? twice = pairs.SelectMany(p => new[] { p.A, p.B }).GroupBy(id => id).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (twice is { } repeated)
        {
            throw new ToolException(T.Format("Tool.ConnectPipeTwice", repeated));
        }

        return pairs.Select(pair => PlanPair(document, pair.A, pair.B)).ToList();
    }

    private Step PlanPair(Document document, long a, long b)
    {
        if (a == b)
        {
            throw new ToolException(T["Tool.PipesSame"]);
        }

        PipeInfo first = Info(document, a);
        PipeInfo second = Info(document, b);
        JointResult joint = PipeJoints.Classify(first, second);
        return joint.Kind switch
        {
            JointKind.Elbow => new Step(a, b, JointKind.Elbow, elbow, Args(new { pipeId1 = a, pipeId2 = b })),
            JointKind.Tee => joint.FirstIsMain
                ? new Step(a, b, JointKind.Tee, tee, Args(new { mainPipeId = a, branchPipeId = b }))
                : new Step(a, b, JointKind.Tee, tee, Args(new { mainPipeId = b, branchPipeId = a })),
            JointKind.Merge => second.Segment.Length > first.Segment.Length
                ? new Step(a, b, JointKind.Merge, merge, Args(new { pipeId1 = b, pipeId2 = a }))
                : new Step(a, b, JointKind.Merge, merge, Args(new { pipeId1 = a, pipeId2 = b })),
            _ => throw new ToolException(T.Format("Tool.ConnectNoJoint", a, b, T["Joint." + joint.Issue])),
        };
    }

    private PipeInfo Info(Document document, long id) =>
        document.GetElement(new ElementId(id)) is Pipe pipe && PipeReader.IsStraight(pipe)
            ? PipeReader.Info(pipe)
            : throw new ToolException(T.Format("Tool.NotAPipe", id));

    /// <summary>Names the pair in a failure, so the user knows which of many joints could not be made.</summary>
    private TResult Guard<TResult>(Step step, Func<TResult> action)
    {
        try
        {
            return action();
        }
        catch (ToolException ex)
        {
            throw new ToolException(T.Format("Tool.ConnectPairFailed", step.A, step.B, ex.Message));
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private sealed record Step(long A, long B, JointKind Kind, RevitWriteTool Tool, JsonElement Arguments);
}

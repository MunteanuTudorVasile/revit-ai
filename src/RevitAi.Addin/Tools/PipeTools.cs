using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
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

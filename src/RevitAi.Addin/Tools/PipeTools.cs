using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Localization;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// MEP pipes (ADR-047). The corner geometry is Revit-free (PipeCorner); Revit inserts the elbow from the pipe type's
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

    private static Segment3 Segment(Pipe pipe)
    {
        Curve curve = ((LocationCurve)pipe.Location).Curve;
        return new Segment3(Mm(curve.GetEndPoint(0)), Mm(curve.GetEndPoint(1)));
    }

    private static Point3 Mm(XYZ p) =>
        new(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters),
            UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters),
            UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Millimeters));

    private static void MoveEnd(Pipe pipe, int end, XYZ point)
    {
        var location = (LocationCurve)pipe.Location;
        Curve curve = location.Curve;
        location.Curve = end == 1
            ? Line.CreateBound(curve.GetEndPoint(0), point)
            : Line.CreateBound(point, curve.GetEndPoint(1));
    }

    private static Connector NearestConnector(Pipe pipe, XYZ point) =>
        pipe.ConnectorManager.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(point))
            .First();

    private static string Diameter(Pipe pipe) => WriteArgs.Mm(UnitUtils.ConvertFromInternalUnits(pipe.Diameter, UnitTypeId.Millimeters));

    private sealed record Corner(Pipe First, Pipe Second, CornerPlan Plan);
}

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

public sealed class GetProjectInfoTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "get_project_info";

    public override string Description =>
        "Returns the open project's title, number, name, Revit version and the display units the project uses for length and area.";

    public override string ProgressLabel => "Reading project information…";

    protected override string SchemaJson => NoArguments;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        Units units = document.GetUnits();
        return new ProjectInfoResult(
            Title: document.Title,
            ProjectNumber: RevitRead.NullIfEmpty(document.ProjectInformation?.Number),
            ProjectName: RevitRead.NullIfEmpty(document.ProjectInformation?.Name),
            RevitVersion: app.Application.VersionNumber,
            LengthDisplayUnit: LabelUtils.GetLabelForUnit(units.GetFormatOptions(SpecTypeId.Length).GetUnitTypeId()),
            AreaDisplayUnit: LabelUtils.GetLabelForUnit(units.GetFormatOptions(SpecTypeId.Area).GetUnitTypeId()));
    }
}

public sealed class GetActiveViewTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "get_active_view";

    public override string Description => "Returns the view the user is looking at: ID, name, view type and its level (if any).";

    public override string ProgressLabel => "Reading the active view…";

    protected override string SchemaJson => NoArguments;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        View view = RevitRead.RequireDocument(app).ActiveView ?? throw new ToolException("There is no active view.");
        Level? level = view.GenLevel;
        return new ViewResult(view.Id.Value, view.Name, view.ViewType.ToString(), level?.Id.Value, level?.Name);
    }
}

public sealed class GetActiveLevelTool(RevitDispatcher dispatcher) : RevitReadTool(dispatcher)
{
    public override string Name => "get_active_level";

    public override string Description =>
        "Returns the level of the active view (ID, name, elevation in mm). Views such as 3D views, sections and sheets have no level.";

    public override string ProgressLabel => "Reading the active level…";

    protected override string SchemaJson => NoArguments;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        View view = RevitRead.RequireDocument(app).ActiveView ?? throw new ToolException("There is no active view.");
        Level? level = view.GenLevel;
        return level is null
            ? new ActiveLevelResult(null, $"The active view '{view.Name}' ({view.ViewType}) is not associated with a level.")
            : new ActiveLevelResult(new LevelResult(level.Id.Value, level.Name, RevitRead.Mm(level.Elevation)), null);
    }
}

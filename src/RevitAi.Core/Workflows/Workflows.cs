using System.Text.Json;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Workflows;

public sealed record Workflow(string Name, string Purpose, IReadOnlyList<string> Steps);

/// <summary>
/// High-level workflows (ADR-014, ADR-039): fixed, reviewed recipes the AI follows step by step for multi-step requests.
/// The recipes only combine existing tools; every change still goes into one plan that the user previews and applies.
/// </summary>
public static class WorkflowCatalog
{
    public static IReadOnlyList<Workflow> All { get; } =
    [
        new("prepare_floor_for_documentation",
            "Make one level ready for documentation: plan view, tags, room dimensions and a sheet.",
            [
                "Call get_project_standards: note the FloorPlan view template, the FloorPlan view name pattern and the sheet number pattern.",
                "Find the level's floor plan with find_views (viewType FloorPlan) and the level ID with find_elements (category Levels). If several plans exist for the level, ask which one; if none, plan create_view (FloorPlan) named per the pattern.",
                "If a FloorPlan template is required and the view does not use it, plan apply_view_template (find the template with find_views templatesOnly true).",
                "Check what is missing first: find_rooms_without_tags for the view. Plan tag_elements for Rooms, then for Doors and Windows (category mode skips already tagged ones).",
                "Plan dimension_room for each enclosed room in the view, at most 10 rooms; if there are more, ask before adding the rest.",
                "Find used sheet numbers with find_views (viewType DrawingSheet); choose the next free number that matches the pattern. Plan create_sheet with the view (use $opN.elementId if the view is created in this plan).",
                "Put everything in ONE plan. Summarise it as counts per step and tell the user that a preview is required before Apply.",
            ]),
        new("qa_floor",
            "Check one level or view for common problems and report them.",
            [
                "Run the checks for the current view (the context's viewId) or the named level: find_rooms_without_tags (viewId), find_unhosted_doors, find_unhosted_windows, find_duplicate_elements, check_standards and get_model_warnings.",
                "Report a short summary: the count per check, then the 3-5 most important issues with element IDs.",
                "Offer select_elements so the user can see the problems.",
                "Only if the user asks to fix: plan tag_elements for untagged rooms; set_parameters only where the user gives the values; delete_elements only when the user explicitly asks to delete.",
            ]),
        new("create_room_with_walls",
            "Create a rectangular room enclosed by new walls, optionally with a door.",
            [
                "Get the size, position (a corner point or a reference element) and level; ask if any is missing. Never invent coordinates.",
                "Call get_project_standards: use the standard wall and door types and an allowed room name.",
                "Plan create_wall four times, corner to corner, so the walls form a closed rectangle.",
                "Plan create_room at the centre of the rectangle with the room name.",
                "If a door is wanted, plan create_door in the requested wall (use $opN.elementId) centred unless told otherwise.",
                "Put everything in ONE plan and say which types and name were used and why.",
            ]),
        new("create_sheet_set",
            "Create a floor plan and a sheet for each level.",
            [
                "List levels with find_elements (category Levels); if there are more than 10, ask which ones.",
                "Call get_project_standards: FloorPlan view template, view name pattern, sheet number pattern.",
                "For each level: reuse its existing floor plan (find_views FloorPlan) if it is not already on a sheet; otherwise plan create_view (FloorPlan) named per the pattern with the template.",
                "Number sheets sequentially from the next free number matching the pattern (find_views viewType DrawingSheet shows used numbers). Plan one create_sheet per level placing its plan.",
                "Put everything in ONE plan; it will require a preview.",
            ]),
        new("fix_standards",
            "Bring the model in line with the company standards where the correct value is unambiguous.",
            [
                "Call check_standards and get_project_standards.",
                "Fixable without guessing: view templates (apply_view_template with the required template), missing parameter values only when the user provides them (set_parameters).",
                "Not fixable without the user: room names, sheet numbers and view names that break the rules (the right new name is a decision) and non-standard types. List these with element IDs and suggested names, and ask before planning set_parameters renames.",
                "Never delete anything as part of this workflow.",
                "Put the agreed fixes in ONE plan.",
            ]),
    ];

    public static Workflow? Find(string name) => All.FirstOrDefault(w => w.Name == name);
}

/// <summary>Returns a workflow recipe. Revit-free, so it lives in Core and runs without the dispatcher.</summary>
public sealed class GetWorkflowTool : IReadTool
{
    public string Name => "get_workflow";

    public string Description =>
        "Returns the step-by-step recipe for a larger task. Call it first and follow the steps when the user asks for one of: " +
        string.Join("; ", WorkflowCatalog.All.Select(w => $"{w.Name} ({w.Purpose})"));

    public JsonElement InputSchema { get; } = ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "enum": [{{string.Join(", ", WorkflowCatalog.All.Select(w => $"\"{w.Name}\""))}}] }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """);

    public RiskLevel Risk => RiskLevel.ReadOnly;

    public string ProgressLabel => "Reading the workflow steps…";

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string name = arguments.GetProperty("name").GetString()!;
        return Task.FromResult<object>(WorkflowCatalog.Find(name) ?? throw new ToolException($"Unknown workflow '{name}'."));
    }
}

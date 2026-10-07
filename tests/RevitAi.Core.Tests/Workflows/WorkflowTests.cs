using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAi.Core.Tools;
using RevitAi.Core.Workflows;

namespace RevitAi.Core.Tests.Workflows;

public class WorkflowTests
{
    // Every tool the recipes may mention. Keep in sync with the tools registered in RevitAi.Addin/App.cs.
    private static readonly HashSet<string> KnownTools =
    [
        "get_project_info", "get_active_view", "get_active_level", "get_selected_elements", "get_element", "find_elements",
        "get_element_parameters", "find_family_types", "get_project_standard_types", "find_nearby_elements", "get_element_room",
        "get_room_boundary", "find_views", "find_rooms_without_tags", "find_unhosted_doors", "find_unhosted_windows",
        "find_duplicate_elements", "get_model_warnings", "find_nonstandard_elements", "find_elements_missing_parameter",
        "select_elements", "get_project_standards", "check_standards", "get_workflow",
        "create_wall", "modify_wall", "move_elements", "delete_elements", "create_room", "create_door", "create_window",
        "create_floor", "create_view", "create_section", "create_elevations", "create_3d_view", "create_sheet",
        "create_schedule", "tag_elements", "create_text", "dimension_wall", "dimension_room", "set_parameters",
        "apply_view_template", "report_unavailable_request", "find_grid_lines", "create_grids", "place_family_instances", "connect_pipes_with_elbow", "merge_pipes", "connect_pipes_with_tee", "query_elements", "check_pipe_systems",
    ];

    [Fact]
    public void Recipes_only_mention_existing_tools()
    {
        foreach (Workflow workflow in WorkflowCatalog.All)
        {
            IEnumerable<string> mentioned = workflow.Steps
                .SelectMany(step => Regex.Matches(step, @"\b[a-z]+(?:_[a-z0-9]+)+\b").Select(m => m.Value))
                .Where(word => word.Contains('_'));
            Assert.All(mentioned, tool => Assert.Contains(tool, KnownTools));
        }
    }

    [Fact]
    public void Names_are_unique_and_snake_case()
    {
        Assert.Equal(WorkflowCatalog.All.Count, WorkflowCatalog.All.Select(w => w.Name).Distinct().Count());
        Assert.All(WorkflowCatalog.All, w => Assert.Matches("^[a-z][a-z0-9_]+$", w.Name));
    }

    [Fact]
    public void Tool_is_registrable_and_returns_the_recipe()
    {
        var tool = new GetWorkflowTool();
        new ToolRegistry().Register(tool);

        Assert.Empty(SchemaValidator.Validate(tool.InputSchema, ToolSchema.Parse("""{ "name": "qa_floor" }""")));
        Assert.NotEmpty(SchemaValidator.Validate(tool.InputSchema, ToolSchema.Parse("""{ "name": "make_coffee" }""")));
    }

    [Fact]
    public async Task Returns_the_requested_workflow()
    {
        object result = await new GetWorkflowTool().ExecuteAsync(ToolSchema.Parse("""{ "name": "qa_floor" }"""), CancellationToken.None);

        Assert.Equal("qa_floor", Assert.IsType<Workflow>(result).Name);
    }

    [Fact]
    public void Schema_enum_lists_every_workflow()
    {
        JsonElement names = new GetWorkflowTool().InputSchema.GetProperty("properties").GetProperty("name").GetProperty("enum");

        Assert.Equal(WorkflowCatalog.All.Select(w => w.Name), names.EnumerateArray().Select(n => n.GetString()));
    }
}

using RevitAi.Core.Standards;

namespace RevitAi.Core.Tests.Standards;

public sealed class ProjectStandardsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());

    private string FilePath => Path.Combine(_dir, "standards.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private ProjectStandards Load(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
        ProjectStandards standards = ProjectStandards.Load(FilePath, out string? problem);
        Assert.Null(problem);
        return standards;
    }

    [Fact]
    public void Missing_file_creates_empty_template()
    {
        ProjectStandards standards = ProjectStandards.Load(FilePath, out string? problem);

        Assert.Null(problem);
        Assert.True(File.Exists(FilePath));
        Assert.Empty(standards.PreferredFor("House.rvt", "Walls"));
    }

    [Fact]
    public void Project_list_overrides_default_for_its_categories_only()
    {
        ProjectStandards standards = Load("""
            {
              "default": { "Walls": ["Basic Wall: Interior - 100mm"], "Doors": ["Single-Flush: 0915 x 2134mm"] },
              "projects": { "House": { "walls": ["Interior - 125mm", "Interior - 100mm"] } }
            }
            """);

        Assert.Equal(["Interior - 125mm", "Interior - 100mm"], standards.PreferredFor("House.rvt", "Walls"));
        Assert.Equal(["Single-Flush: 0915 x 2134mm"], standards.PreferredFor("House.rvt", "Doors"));
        Assert.Equal(["Basic Wall: Interior - 100mm"], standards.PreferredFor("Office.rvt", "Walls"));
        Assert.Empty(standards.PreferredFor("House.rvt", "Windows"));
    }

    [Fact]
    public void Malformed_file_reports_problem_and_is_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ nope");

        ProjectStandards standards = ProjectStandards.Load(FilePath, out string? problem);

        Assert.NotNull(problem);
        Assert.Empty(standards.PreferredFor("House.rvt", "Walls"));
    }

    [Theory]
    [InlineData("Basic Wall: Interior - 100mm", true)]
    [InlineData("basic wall: interior - 100MM", true)]
    [InlineData("Interior - 100mm", true)]
    [InlineData(" Interior - 100mm ", true)]
    [InlineData("Basic Wall", false)]
    [InlineData("Curtain Wall: Interior - 100mm", false)]
    public void Matches_family_and_type_or_type_alone(string entry, bool expected)
    {
        Assert.Equal(expected, ProjectStandards.Matches(entry, "Basic Wall", "Interior - 100mm"));
    }

    [Fact]
    public void Project_rules_override_only_the_rules_they_set()
    {
        ProjectStandards standards = Load("""
            {
              "rules": { "roomNames": ["Living", "Bedroom"], "sheetNumberPattern": "^A\\d{3}$" },
              "projectRules": { "House": { "sheetNumberPattern": "^H-\\d{2}$" } }
            }
            """);

        StandardsRules house = standards.RulesFor("House.rvt", out IReadOnlyList<string> problems);
        StandardsRules office = standards.RulesFor("Office.rvt", out _);

        Assert.Empty(problems);
        Assert.Equal("^H-\\d{2}$", house.SheetNumberPattern);
        Assert.Equal(["Living", "Bedroom"], house.RoomNames);
        Assert.Equal("^A\\d{3}$", office.SheetNumberPattern);
    }

    [Fact]
    public void Invalid_patterns_are_dropped_and_reported()
    {
        ProjectStandards standards = Load("""
            { "rules": { "sheetNumberPattern": "^A(", "viewNamePatterns": { "FloorPlan": "[", "Section": "^S" } } }
            """);

        StandardsRules rules = standards.RulesFor("House.rvt", out IReadOnlyList<string> problems);

        Assert.Null(rules.SheetNumberPattern);
        Assert.Equal(["Section"], rules.ViewNamePatterns!.Keys);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Empty_file_has_no_rules()
    {
        Assert.True(Load("{}").RulesFor("House.rvt", out _).IsEmpty);
    }

    [Theory]
    [InlineData("Bedroom", true)]
    [InlineData(" bedroom ", true)]
    [InlineData("Bed room", false)]
    [InlineData(null, false)]
    public void Room_names_are_checked_against_the_list(string? name, bool expected)
    {
        var rules = new StandardsRules { RoomNames = ["Living", "Bedroom"] };

        Assert.Equal(expected, ProjectStandards.IsAllowedRoomName(rules, name));
    }

    [Fact]
    public void Any_room_name_is_allowed_without_a_list()
    {
        Assert.True(ProjectStandards.IsAllowedRoomName(new StandardsRules(), "Whatever"));
    }

    [Theory]
    [InlineData("A101", true)]
    [InlineData("A1011", false)]
    [InlineData("B101", false)]
    [InlineData(null, false)]
    public void Sheet_numbers_are_matched_against_the_pattern(string? number, bool expected)
    {
        Assert.Equal(expected, ProjectStandards.MatchesPattern("^A\\d{3}$", number));
    }

    [Fact]
    public void Pattern_too_slow_to_evaluate_returns_null()
    {
        Assert.Null(ProjectStandards.MatchesPattern("^(a+)+$", new string('a', 40) + "!"));
    }

    [Fact]
    public void Template_file_has_no_computed_or_null_rules()
    {
        ProjectStandards.Load(FilePath, out _);

        string template = File.ReadAllText(FilePath);
        Assert.DoesNotContain("isEmpty", template);
        Assert.DoesNotContain("null", template);
    }

    [Fact]
    public void Type_categories_include_default_and_project_lists()
    {
        ProjectStandards standards = Load("""
            { "default": { "Walls": ["Interior"] }, "projects": { "House": { "Doors": ["Single"] } } }
            """);

        Assert.Equal(["Walls", "Doors"], standards.TypeCategories("House.rvt"));
        Assert.Equal(["Walls"], standards.TypeCategories("Office.rvt"));
    }
}

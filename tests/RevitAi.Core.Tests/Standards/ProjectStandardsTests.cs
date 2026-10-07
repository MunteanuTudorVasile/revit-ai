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
}

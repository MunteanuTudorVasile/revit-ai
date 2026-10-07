using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void Registers_and_finds_read_only_tool()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool();

        registry.Register(tool);

        Assert.True(registry.TryGet("get_thing", out ITool found));
        Assert.Same(tool, found);
        Assert.False(registry.TryGet("missing", out _));
    }

    [Theory]
    [InlineData(RiskLevel.SafeModification)]
    [InlineData(RiskLevel.LargeModification)]
    [InlineData(RiskLevel.Destructive)]
    public void Rejects_read_tools_that_are_not_read_only(RiskLevel risk)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry().Register(new FakeTool("get_wall", risk)));
    }

    [Theory]
    [InlineData(RiskLevel.SafeModification)]
    [InlineData(RiskLevel.LargeModification)]
    public void Accepts_write_tools(RiskLevel risk)
    {
        var registry = new ToolRegistry();

        registry.Register(new FakeWriteTool(risk: risk));

        Assert.True(registry.TryGet("create_thing", out _));
    }

    [Theory]
    [InlineData(RiskLevel.ReadOnly)]
    [InlineData(RiskLevel.Destructive)]
    public void Rejects_write_tools_that_are_read_only_or_destructive(RiskLevel risk)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry().Register(new FakeWriteTool(risk: risk)));
    }

    [Fact]
    public void Rejects_duplicate_names()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool());

        Assert.Throws<ArgumentException>(() => registry.Register(new FakeTool()));
    }

    [Theory]
    [InlineData("GetThing")]
    [InlineData("get-thing")]
    [InlineData("")]
    public void Rejects_names_that_are_not_snake_case(string name)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry().Register(new FakeTool(name)));
    }

    [Fact]
    public void Rejects_non_object_schema()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry().Register(new FakeTool(schema: """{ "type": "string" }""")));
    }
}

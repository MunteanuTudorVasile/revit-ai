using System.Text.Json;
using RevitAi.Core.Ai;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Tests.Ai;

public sealed class UnmetRequestsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());

    private string FilePath => Path.Combine(_dir, "unmet-requests.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Tool_appends_one_line_per_request()
    {
        var tool = new ReportUnavailableRequestTool(new UnmetRequestLog(FilePath, new FileLog(_dir)));
        new ToolRegistry().Register(tool);

        await tool.ExecuteAsync(ToolSchema.Parse("""{ "request": "Mută peretele spre est", "missingCapability": "rotate walls" }"""), CancellationToken.None);
        await tool.ExecuteAsync(ToolSchema.Parse("""{ "request": "Read the point cloud", "missingCapability": "point clouds" }"""), CancellationToken.None);

        string[] lines = File.ReadAllLines(FilePath);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Mută peretele spre est", first.RootElement.GetProperty("request").GetString());
        Assert.Equal("rotate walls", first.RootElement.GetProperty("missingCapability").GetString());
    }
}

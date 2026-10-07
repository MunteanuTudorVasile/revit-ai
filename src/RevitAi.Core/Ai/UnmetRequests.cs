using System.Text.Encodings.Web;
using System.Text.Json;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

public sealed record UnmetRequest(DateTimeOffset Timestamp, string Request, string MissingCapability);

/// <summary>
/// Records requests the assistant could not fulfil because no tool supports them, so the product backlog follows real
/// demand (ADR-043). Stored locally only, as JSON Lines next to the action history.
/// </summary>
public sealed class UnmetRequestLog
{
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly FileLog _log;
    private readonly Func<DateTimeOffset> _now;

    public UnmetRequestLog(string filePath, FileLog log, Func<DateTimeOffset>? now = null)
    {
        _filePath = filePath;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Record(string request, string missingCapability)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.AppendAllText(_filePath,
                JsonSerializer.Serialize(new UnmetRequest(_now(), request, missingCapability), LineJson) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not record an unmet request.", ex);
        }
    }
}

/// <summary>The AI calls this when it has to tell the user something is not available. Revit-free.</summary>
public sealed class ReportUnavailableRequestTool(UnmetRequestLog log) : IReadTool
{
    public string Name => "report_unavailable_request";

    public string Description =>
        "Call this once, before answering, whenever the user asks for something you cannot do because no tool supports it " +
        "(not for missing information or ambiguous requests). It only records the request for the product team; it changes nothing.";

    public JsonElement InputSchema { get; } = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "request": { "type": "string", "description": "What the user asked, in a short sentence." },
            "missingCapability": { "type": "string", "description": "What capability is missing, e.g. 'move a wall sideways', 'read point clouds'." }
          },
          "required": ["request", "missingCapability"],
          "additionalProperties": false
        }
        """);

    public RiskLevel Risk => RiskLevel.ReadOnly;

    public string ProgressLabel => "Noting the request…";

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        log.Record(arguments.GetProperty("request").GetString()!, arguments.GetProperty("missingCapability").GetString()!);
        return Task.FromResult<object>(new { recorded = true });
    }
}

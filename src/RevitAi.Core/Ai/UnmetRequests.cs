using System.Text.Encodings.Web;
using System.Text.Json;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Ai;

/// <param name="Area">One of <see cref="UnmetRequestLog.Areas"/>; null in lines written before areas existed.</param>
public sealed record UnmetRequest(DateTimeOffset Timestamp, string Request, string MissingCapability, string? Area = null);

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

    public const string FileName = "unmet-requests.jsonl";

    public static readonly IReadOnlyList<string> Areas =
        ["piping", "hvac", "electrical", "architecture", "structure", "documentation", "data", "other"];

    private readonly string _filePath;
    private readonly FileLog _log;
    private readonly Func<DateTimeOffset> _now;

    public UnmetRequestLog(string filePath, FileLog log, Func<DateTimeOffset>? now = null)
    {
        _filePath = filePath;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Record(string request, string missingCapability, string? area = null)
    {
        area = area is not null && Areas.Contains(area) ? area : "other";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.AppendAllText(_filePath,
                JsonSerializer.Serialize(new UnmetRequest(_now(), request, missingCapability, area), LineJson) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not record an unmet request.", ex);
        }
    }
}

/// <summary>Reads the recorded requests back for the summary (ADR-051); unreadable lines are skipped.</summary>
public static class UnmetRequestFile
{
    private static readonly JsonSerializerOptions ReadJson = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<UnmetRequest> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var requests = new List<UnmetRequest>();
        foreach (string line in File.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<UnmetRequest>(line, ReadJson) is { Request: not null, MissingCapability: not null } request)
                {
                    requests.Add(request);
                }
            }
            catch (JsonException)
            {
                // A damaged line does not hide the others.
            }
        }

        return requests;
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
            "missingCapability": { "type": "string", "description": "What capability is missing, in a few words that would be the same for similar requests, e.g. 'reducer between pipe sizes', 'read point clouds'." },
            "area": { "type": "string", "enum": ["piping", "hvac", "electrical", "architecture", "structure", "documentation", "data", "other"], "description": "Which area the capability belongs to." }
          },
          "required": ["request", "missingCapability", "area"],
          "additionalProperties": false
        }
        """);

    public RiskLevel Risk => RiskLevel.ReadOnly;

    public string ProgressLabel => "Noting the request…";

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        log.Record(
            arguments.GetProperty("request").GetString()!,
            arguments.GetProperty("missingCapability").GetString()!,
            arguments.TryGetProperty("area", out JsonElement area) ? area.GetString() : null);
        return Task.FromResult<object>(new { recorded = true });
    }
}

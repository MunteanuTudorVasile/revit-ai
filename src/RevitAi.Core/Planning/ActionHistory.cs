using System.Text.Encodings.Web;
using System.Text.Json;
using RevitAi.Core.Infrastructure;

namespace RevitAi.Core.Planning;

/// <summary>One Apply attempt, successful or not (CLAUDE.md §12).</summary>
public sealed record ActionRecord(
    Guid ActionId,
    DateTimeOffset Timestamp,
    string DocumentTitle,
    string UserRequest,
    IReadOnlyList<PlannedOperation> Operations,
    PlanRunResult Result);

/// <summary>
/// AI action history: kept in memory for context and explanations, and appended to a JSON Lines file for auditing.
/// </summary>
public sealed class ActionHistory
{
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<ActionRecord> _records = [];
    private readonly string? _filePath;
    private readonly FileLog _log;

    public ActionHistory(string? filePath, FileLog log)
    {
        _filePath = filePath;
        _log = log;
    }

    public IReadOnlyList<ActionRecord> Records => _records;

    public void Add(ActionRecord record)
    {
        _records.Add(record);
        if (_filePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.AppendAllText(_filePath, JsonSerializer.Serialize(record, LineJson) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not write action {record.ActionId} to the history file.", ex);
        }
    }

    /// <summary>The most recent successfully applied actions in a document, newest last.</summary>
    public IReadOnlyList<ActionRecord> RecentApplied(string? documentTitle, int count) =>
        _records.Where(r => r.Result.Applied && r.DocumentTitle == documentTitle).TakeLast(count).ToList();
}

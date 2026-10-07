using System.Globalization;
using System.Text;

namespace RevitAi.Core.Ai;

public sealed record UnmetCapability(string Area, string Capability, int Count, DateTimeOffset LastAsked, IReadOnlyList<string> Examples);

/// <summary>
/// Groups the requests the assistant could not do by area and missing capability, most asked first, for the Requests ribbon
/// button (ADR-051). This is the backlog from real use.
/// </summary>
public static class UnmetRequestSummary
{
    private const int MaxExamples = 3;

    private static readonly IReadOnlyDictionary<string, string> AreaNames = new Dictionary<string, string>
    {
        ["piping"] = "Piping",
        ["hvac"] = "HVAC / ducts",
        ["electrical"] = "Electrical",
        ["architecture"] = "Architecture",
        ["structure"] = "Structure",
        ["documentation"] = "Documentation (views, sheets, tags, dimensions)",
        ["data"] = "Data, schedules and export",
        ["other"] = "Other",
    };

    public static IReadOnlyList<UnmetCapability> Group(IEnumerable<UnmetRequest> requests) =>
        requests
            .GroupBy(r => (Area: r.Area ?? "other", Capability: r.MissingCapability.Trim().ToLowerInvariant()))
            .Select(g => new UnmetCapability(
                g.Key.Area,
                g.First().MissingCapability.Trim(),
                g.Count(),
                g.Max(r => r.Timestamp),
                g.OrderByDescending(r => r.Timestamp).Select(r => r.Request.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxExamples).ToList()))
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.LastAsked)
            .ToList();

    public static string AreaName(string area) => AreaNames.TryGetValue(area, out string? name) ? name : area;

    public static string Format(IReadOnlyList<UnmetRequest> requests, DateTimeOffset when)
    {
        IReadOnlyList<UnmetCapability> capabilities = Group(requests);
        var text = new StringBuilder();
        text.AppendLine("# Requests Revit AI could not do yet");
        text.AppendLine();
        text.AppendLine($"- Summary made: {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        if (requests.Count > 0)
        {
            text.AppendLine($"- {requests.Count} request(s), {capabilities.Count} different missing capabilities, " +
                $"from {Day(requests.Min(r => r.Timestamp))} to {Day(requests.Max(r => r.Timestamp))}");
        }

        text.AppendLine("- Recorded on this computer only, when the assistant had to say something is not available.");

        foreach (IGrouping<string, UnmetCapability> area in capabilities.GroupBy(c => c.Area).OrderByDescending(g => g.Sum(c => c.Count)))
        {
            text.AppendLine();
            text.AppendLine($"## {AreaName(area.Key)} ({area.Sum(c => c.Count)})");
            text.AppendLine();
            text.AppendLine("| Missing capability | Times asked | Last asked | Example requests |");
            text.AppendLine("|---|---:|---|---|");
            foreach (UnmetCapability capability in area)
            {
                text.AppendLine($"| {Cell(capability.Capability)} | {capability.Count} | {Day(capability.LastAsked)} | " +
                    $"{Cell(string.Join(" · ", capability.Examples))} |");
            }
        }

        return text.ToString();
    }

    private static string Day(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}

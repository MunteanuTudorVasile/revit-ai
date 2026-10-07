using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RevitAi.Core.Standards;

/// <summary>
/// Company/project standards, edited by the user in standards.json (ADR-010, ADR-033, ADR-038).
/// The AI reads them through tools; it never changes them.
/// <code>
/// {
///   "default":  { "Walls": ["Basic Wall: Interior - 100mm"], "Doors": ["Single-Flush: 0915 x 2134mm"] },
///   "projects": { "House": { "Walls": ["Basic Wall: Interior - 125mm"] } },
///   "rules": {
///     "roomNames": ["Living", "Kitchen", "Bedroom", "Bathroom"],
///     "sheetNumberPattern": "^A\\d{3}$",
///     "viewNamePatterns": { "FloorPlan": "^Level \\d+ - " },
///     "requiredParameters": { "Doors": ["Mark", "Fire Rating"] },
///     "viewTemplates": { "FloorPlan": "Architectural Plan" }
///   },
///   "projectRules": { "House": { "sheetNumberPattern": "^H-\\d{2}$" } }
/// }
/// </code>
/// Type entries are "Family: Type" or just "Type", in order of preference. A project entry (document title without ".rvt")
/// replaces the default list for the categories it names; project rules replace the default rules they set.
/// </summary>
public sealed class ProjectStandards
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly StandardsFile _file;

    private ProjectStandards(StandardsFile file)
    {
        _file = file;
    }

    public static ProjectStandards Empty { get; } = new(new StandardsFile());

    /// <summary>Loads the file; a missing file is created as an empty template. Problems fall back to empty.</summary>
    public static ProjectStandards Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new StandardsFile(), JsonOptions));
                return Empty;
            }

            StandardsFile file = JsonSerializer.Deserialize<StandardsFile>(File.ReadAllText(path), JsonOptions)
                ?? new StandardsFile();
            return new ProjectStandards(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"Could not read project standards from {path}: {ex.Message}";
            return Empty;
        }
    }

    /// <summary>Preferred "Family: Type" or "Type" entries for a category, most preferred first.</summary>
    public IReadOnlyList<string> PreferredFor(string? documentTitle, string category)
    {
        if (Project(documentTitle) is { } project
            && Find(_file.Projects, project) is { } projectLists
            && Find(projectLists, category) is { } projectPreferred)
        {
            return projectPreferred;
        }

        return Find(_file.Default, category) ?? [];
    }

    /// <summary>Categories that have standard types for this document (default or project).</summary>
    public IReadOnlyList<string> TypeCategories(string? documentTitle)
    {
        IEnumerable<string> categories = _file.Default?.Keys ?? Enumerable.Empty<string>();
        if (Project(documentTitle) is { } project && Find(_file.Projects, project) is { } projectLists)
        {
            categories = categories.Concat(projectLists.Keys);
        }

        return categories.Distinct(StringComparer.OrdinalIgnoreCase).Where(c => PreferredFor(documentTitle, c).Count > 0).ToList();
    }

    /// <summary>
    /// The rules that apply to a document: project rules override the default rules they set.
    /// Invalid regular expressions are dropped and reported in <paramref name="problems"/>.
    /// </summary>
    public StandardsRules RulesFor(string? documentTitle, out IReadOnlyList<string> problems)
    {
        StandardsRules defaults = _file.Rules ?? new StandardsRules();
        StandardsRules? project = Project(documentTitle) is { } name ? Find(_file.ProjectRules, name) : null;

        var merged = new StandardsRules
        {
            RoomNames = project?.RoomNames ?? defaults.RoomNames,
            SheetNumberPattern = project?.SheetNumberPattern ?? defaults.SheetNumberPattern,
            ViewNamePatterns = (project?.ViewNamePatterns ?? defaults.ViewNamePatterns) is { } patterns
                ? new Dictionary<string, string>(patterns, StringComparer.OrdinalIgnoreCase)
                : null,
            RequiredParameters = project?.RequiredParameters ?? defaults.RequiredParameters,
            ViewTemplates = project?.ViewTemplates ?? defaults.ViewTemplates,
        };

        var found = new List<string>();
        if (merged.SheetNumberPattern is { } sheetPattern && !IsValidPattern(sheetPattern))
        {
            found.Add($"sheetNumberPattern '{sheetPattern}' is not a valid regular expression; it is ignored.");
            merged.SheetNumberPattern = null;
        }

        if (merged.ViewNamePatterns is { } viewPatterns)
        {
            foreach ((string viewType, string pattern) in viewPatterns.Where(p => !IsValidPattern(p.Value)).ToList())
            {
                found.Add($"viewNamePatterns.{viewType} '{pattern}' is not a valid regular expression; it is ignored.");
                viewPatterns.Remove(viewType);
            }
        }

        problems = found;
        return merged;
    }

    /// <summary>True when <paramref name="entry"/> names this type, as "Family: Type" or just "Type" (case-insensitive).</summary>
    public static bool Matches(string entry, string? family, string type)
    {
        string trimmed = entry.Trim();
        return string.Equals(trimmed, type, StringComparison.OrdinalIgnoreCase)
               || (family is not null && string.Equals(trimmed, $"{family}: {type}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the room name is in the allowed list (case-insensitive), or no list is configured.</summary>
    public static bool IsAllowedRoomName(StandardsRules rules, string? name) =>
        rules.RoomNames is not { Count: > 0 } allowed
        || (name is not null && allowed.Any(a => string.Equals(a.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// True when <paramref name="value"/> matches the pattern, false when not, and null when the pattern took too long
    /// to evaluate (the caller must report that rather than treat it as passing). Patterns are validated by <see cref="RulesFor"/>.
    /// </summary>
    public static bool? MatchesPattern(string pattern, string? value)
    {
        try
        {
            return value is not null && Regex.IsMatch(value, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? Project(string? documentTitle) =>
        documentTitle is null ? null
        : documentTitle.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? documentTitle[..^4]
        : documentTitle;

    private static T? Find<T>(Dictionary<string, T>? map, string key)
        where T : class =>
        map?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class StandardsFile
    {
        public Dictionary<string, List<string>>? Default { get; set; } = [];

        public Dictionary<string, Dictionary<string, List<string>>>? Projects { get; set; } = [];

        public StandardsRules? Rules { get; set; } = new();

        public Dictionary<string, StandardsRules>? ProjectRules { get; set; } = [];
    }
}

/// <summary>Company rules checked by check_standards. Every rule is optional; null means "not configured".</summary>
public sealed class StandardsRules
{
    /// <summary>Allowed room names (case-insensitive).</summary>
    public List<string>? RoomNames { get; set; }

    /// <summary>Regular expression every sheet number must match, e.g. "^A\d{3}$".</summary>
    public string? SheetNumberPattern { get; set; }

    /// <summary>Revit view type (e.g. FloorPlan) → regular expression its view names must match.</summary>
    public Dictionary<string, string>? ViewNamePatterns { get; set; }

    /// <summary>Category → parameters that must have a value.</summary>
    public Dictionary<string, List<string>>? RequiredParameters { get; set; }

    /// <summary>Revit view type → name of the view template those views must use.</summary>
    public Dictionary<string, string>? ViewTemplates { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        RoomNames is not { Count: > 0 }
        && SheetNumberPattern is null
        && ViewNamePatterns is not { Count: > 0 }
        && RequiredParameters is not { Count: > 0 }
        && ViewTemplates is not { Count: > 0 };
}

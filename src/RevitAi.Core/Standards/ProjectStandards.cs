using System.Text.Json;

namespace RevitAi.Core.Standards;

/// <summary>
/// Preferred types per category, edited by the user in standards.json (ADR-010, ADR-033).
/// The AI reads them through a tool; it never changes them.
/// <code>
/// {
///   "default":  { "Walls": ["Basic Wall: Interior - 100mm"], "Doors": ["Single-Flush: 0915 x 2134mm"] },
///   "projects": { "House": { "Walls": ["Basic Wall: Interior - 125mm"] } }
/// }
/// </code>
/// Entries are "Family: Type" or just "Type", in order of preference. A project entry (matched by document
/// title, without ".rvt") replaces the default list for the categories it names.
/// </summary>
public sealed class ProjectStandards
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        string? project = documentTitle is null ? null : StripExtension(documentTitle);
        if (project is not null
            && Find(_file.Projects, project) is { } projectLists
            && Find(projectLists, category) is { } projectPreferred)
        {
            return projectPreferred;
        }

        return Find(_file.Default, category) ?? [];
    }

    /// <summary>True when <paramref name="entry"/> names this type, as "Family: Type" or just "Type" (case-insensitive).</summary>
    public static bool Matches(string entry, string? family, string type)
    {
        string trimmed = entry.Trim();
        return string.Equals(trimmed, type, StringComparison.OrdinalIgnoreCase)
               || (family is not null && string.Equals(trimmed, $"{family}: {type}", StringComparison.OrdinalIgnoreCase));
    }

    private static T? Find<T>(Dictionary<string, T>? map, string key)
        where T : class =>
        map?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string StripExtension(string title) =>
        title.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? title[..^4] : title;

    private sealed class StandardsFile
    {
        public Dictionary<string, List<string>>? Default { get; set; } = [];

        public Dictionary<string, Dictionary<string, List<string>>>? Projects { get; set; } = [];
    }
}

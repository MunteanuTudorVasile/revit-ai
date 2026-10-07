using System.Text.Json;

namespace RevitAi.Core.Infrastructure;

/// <summary>User-editable settings, stored as JSON. Secrets never go here (ADR-029).</summary>
public sealed record AddinSettings
{
    public int DispatcherTimeoutSeconds { get; init; } = 30;

    /// <summary>OpenAI model name. Check the current model list on the OpenAI platform.</summary>
    public string OpenAiModel { get; init; } = "gpt-5";

    public int AiRequestTimeoutSeconds { get; init; } = 120;

    /// <summary>Maximum AI round trips per question before the assistant gives up.</summary>
    public int MaxAiSteps { get; init; } = 8;

    /// <summary>When the user accepted the data notice (ADR-030); null until accepted.</summary>
    public DateTimeOffset? ConsentAcceptedAt { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads settings from <paramref name="path"/>. A missing file is created with defaults.
    /// A file that cannot be read or parsed falls back to defaults and returns the reason in <paramref name="problem"/>.
    /// Invalid individual values are replaced by their defaults, also reported in <paramref name="problem"/>.
    /// </summary>
    public static AddinSettings Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new AddinSettings();
                defaults.Save(path);
                return defaults;
            }

            AddinSettings settings = JsonSerializer.Deserialize<AddinSettings>(File.ReadAllText(path))
                ?? throw new JsonException("Settings file is empty.");

            return settings.WithValidValues(out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"Could not load settings from {path}: {ex.Message}. Using defaults.";
            return new AddinSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private AddinSettings WithValidValues(out string? problem)
    {
        var defaults = new AddinSettings();
        var problems = new List<string>();
        AddinSettings result = this;

        if (DispatcherTimeoutSeconds <= 0)
        {
            problems.Add($"DispatcherTimeoutSeconds must be positive; using {defaults.DispatcherTimeoutSeconds}.");
            result = result with { DispatcherTimeoutSeconds = defaults.DispatcherTimeoutSeconds };
        }

        if (AiRequestTimeoutSeconds <= 0)
        {
            problems.Add($"AiRequestTimeoutSeconds must be positive; using {defaults.AiRequestTimeoutSeconds}.");
            result = result with { AiRequestTimeoutSeconds = defaults.AiRequestTimeoutSeconds };
        }

        if (MaxAiSteps <= 0)
        {
            problems.Add($"MaxAiSteps must be positive; using {defaults.MaxAiSteps}.");
            result = result with { MaxAiSteps = defaults.MaxAiSteps };
        }

        if (string.IsNullOrWhiteSpace(OpenAiModel))
        {
            problems.Add($"OpenAiModel is empty; using {defaults.OpenAiModel}.");
            result = result with { OpenAiModel = defaults.OpenAiModel };
        }

        problem = problems.Count == 0 ? null : string.Join(" ", problems);
        return result;
    }
}

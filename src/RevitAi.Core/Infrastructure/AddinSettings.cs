using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAi.Core.Infrastructure;

/// <summary>User-editable settings, stored as JSON. Secrets never go here (ADR-029).</summary>
public sealed record AddinSettings
{
    public int DispatcherTimeoutSeconds { get; init; } = 30;

    /// <summary>AI service: "openai" or "gemini" (ADR-046). Set from the panel.</summary>
    public string AiProvider { get; init; } = Ai.AiProviders.OpenAi.Id;

    /// <summary>Model override; null uses the service's default model. Set from the panel.</summary>
    public string? AiModel { get; init; }

    public int AiRequestTimeoutSeconds { get; init; } = 120;

    /// <summary>Maximum AI round trips per question before the assistant gives up.</summary>
    public int MaxAiSteps { get; init; } = 12;

    /// <summary>Panel language: "en" or "ro".</summary>
    public string Language { get; init; } = "en";

    /// <summary>When the user accepted the data notice (ADR-030); null until accepted.</summary>
    public DateTimeOffset? ConsentAcceptedAt { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads settings from <paramref name="path"/>. A missing file is created (empty: every value is a default; see the README).
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

    /// <summary>
    /// Writes only the values that differ from the defaults, so a later change of a default (e.g. MaxAiSteps)
    /// still reaches users who never set that value themselves.
    /// </summary>
    public void Save(string path)
    {
        JsonObject values = JsonSerializer.SerializeToNode(this)!.AsObject();
        JsonObject defaults = JsonSerializer.SerializeToNode(new AddinSettings())!.AsObject();
        foreach (string key in values.Select(p => p.Key).ToList())
        {
            if (JsonNode.DeepEquals(values[key], defaults[key]))
            {
                values.Remove(key);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, values.ToJsonString(JsonOptions));
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

        if (!Ai.AiProviders.IsKnown(AiProvider))
        {
            problems.Add($"AiProvider must be one of {string.Join(", ", Ai.AiProviders.All.Select(p => p.Id))}; using {defaults.AiProvider}.");
            result = result with { AiProvider = defaults.AiProvider };
        }

        if (AiModel is not null && string.IsNullOrWhiteSpace(AiModel))
        {
            result = result with { AiModel = null };
        }

        if (!Localization.UiText.IsSupported(Language))
        {
            problems.Add($"Language must be one of {string.Join(", ", Localization.UiText.SupportedLanguages)}; using {defaults.Language}.");
            result = result with { Language = defaults.Language };
        }

        problem = problems.Count == 0 ? null : string.Join(" ", problems);
        return result;
    }
}

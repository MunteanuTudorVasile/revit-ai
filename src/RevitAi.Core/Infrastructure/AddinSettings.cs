using System.Text.Json;

namespace RevitAi.Core.Infrastructure;

/// <summary>User-editable settings, stored as JSON. Secrets never go here (ADR-029).</summary>
public sealed record AddinSettings
{
    public int DispatcherTimeoutSeconds { get; init; } = 30;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads settings from <paramref name="path"/>. A missing file is created with defaults.
    /// A file that cannot be read or parsed falls back to defaults and returns the reason in <paramref name="problem"/>.
    /// </summary>
    public static AddinSettings Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new AddinSettings();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
                return defaults;
            }

            AddinSettings settings = JsonSerializer.Deserialize<AddinSettings>(File.ReadAllText(path))
                ?? throw new JsonException("Settings file is empty.");

            if (settings.DispatcherTimeoutSeconds <= 0)
            {
                problem = $"DispatcherTimeoutSeconds must be positive; using {new AddinSettings().DispatcherTimeoutSeconds}.";
                return settings with { DispatcherTimeoutSeconds = new AddinSettings().DispatcherTimeoutSeconds };
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"Could not load settings from {path}: {ex.Message}. Using defaults.";
            return new AddinSettings();
        }
    }
}

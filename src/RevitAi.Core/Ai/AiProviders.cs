namespace RevitAi.Core.Ai;

/// <summary>An AI service that speaks the OpenAI Chat Completions format (ADR-046).</summary>
public sealed record AiProvider(string Id, string DisplayName, Uri Endpoint, string DefaultModel, string KeyUrl);

public static class AiProviders
{
    public static readonly AiProvider OpenAi = new(
        "openai", "OpenAI", new Uri("https://api.openai.com/v1/chat/completions"), "gpt-5", "platform.openai.com/api-keys");

    /// <summary>Google's OpenAI-compatible endpoint; supports tools with strict schemas from Gemini 2.5 on.</summary>
    public static readonly AiProvider Gemini = new(
        "gemini", "Google Gemini", new Uri("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"),
        "gemini-3.8-flash", "aistudio.google.com/apikey");

    public static IReadOnlyList<AiProvider> All { get; } = [OpenAi, Gemini];

    public static bool IsKnown(string? id) => All.Any(p => p.Id == id);

    public static AiProvider Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? OpenAi;

    /// <summary>The service a key obviously belongs to by its prefix (Google keys start with "AIza", OpenAI keys with "sk-"), or null.</summary>
    public static AiProvider? Recognise(string apiKey)
    {
        string key = apiKey.Trim();
        return key.StartsWith("AIza", StringComparison.Ordinal) ? Gemini
            : key.StartsWith("sk-", StringComparison.Ordinal) ? OpenAi
            : null;
    }
}

/// <summary>Where and how to send the next AI request.</summary>
public sealed record AiConnection(AiProvider Provider, string Model, string? ApiKey);

/// <summary>
/// The current provider and model, changed from the panel and read on every request, so switching service applies at once.
/// </summary>
public sealed class AiConnectionSource(Func<string, string?> loadApiKey)
{
    public AiProvider Provider { get; set; } = AiProviders.OpenAi;

    /// <summary>Model override; null means the provider's default model.</summary>
    public string? Model { get; set; }

    public AiConnection Current() =>
        new(Provider, string.IsNullOrWhiteSpace(Model) ? Provider.DefaultModel : Model.Trim(), loadApiKey(Provider.Id));
}

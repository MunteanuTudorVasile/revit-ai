namespace RevitAi.Core.Localization;

/// <summary>
/// The current panel language, shared by the panel and everything that produces user-visible text
/// (write tools, plan executor). Switching language in the panel updates it for all of them.
/// </summary>
public sealed class TextSource(UiText initial)
{
    public UiText Current { get; set; } = initial;
}

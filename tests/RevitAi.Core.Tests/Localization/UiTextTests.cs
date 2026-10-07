using System.Text.RegularExpressions;
using RevitAi.Core.Localization;

namespace RevitAi.Core.Tests.Localization;

public class UiTextTests
{
    private static readonly UiText En = new(UiText.English);
    private static readonly UiText Ro = new(UiText.Romanian);

    [Fact]
    public void Every_english_text_has_a_romanian_translation()
    {
        IEnumerable<string> missing = UiText.EnglishStrings.Keys.Except(UiText.RomanianStrings.Keys);

        Assert.Empty(missing);
    }

    [Fact]
    public void Translations_use_the_same_placeholders()
    {
        foreach ((string key, string english) in UiText.EnglishStrings)
        {
            Assert.True(
                Placeholders(english).SetEquals(Placeholders(UiText.RomanianStrings[key])),
                $"Placeholders differ for '{key}'.");
        }
    }

    [Fact]
    public void Romanian_extras_are_only_progress_labels()
    {
        Assert.All(
            UiText.RomanianStrings.Keys.Except(UiText.EnglishStrings.Keys),
            key => Assert.StartsWith("Progress.", key));
    }

    [Theory]
    [InlineData(0, "0 elemente selectate")]
    [InlineData(1, "1 element selectat")]
    [InlineData(2, "2 elemente selectate")]
    [InlineData(19, "19 elemente selectate")]
    [InlineData(20, "20 de elemente selectate")]
    [InlineData(101, "101 elemente selectate")]
    [InlineData(120, "120 de elemente selectate")]
    public void Romanian_plurals(int count, string expected)
    {
        Assert.Equal(expected, Ro.SelectedElements(count));
    }

    [Theory]
    [InlineData(0, "0 selected elements")]
    [InlineData(1, "1 selected element")]
    [InlineData(20, "20 selected elements")]
    public void English_plurals(int count, string expected)
    {
        Assert.Equal(expected, En.SelectedElements(count));
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        Assert.Equal(UiText.English, new UiText("fr").Language);
    }

    [Fact]
    public void Progress_label_falls_back_to_the_tool_label_in_english()
    {
        var tool = new FakeTool(name: "get_selected_elements");

        Assert.Equal("Running get_selected_elements…", En.ProgressFor(tool));
        Assert.Equal("Citesc selecția…", Ro.ProgressFor(tool));
    }

    [Fact]
    public void Missing_key_returns_the_key()
    {
        Assert.Equal("NoSuchKey", Ro["NoSuchKey"]);
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).ToHashSet();
}

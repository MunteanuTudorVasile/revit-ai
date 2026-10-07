using System.Globalization;
using System.Text;

namespace RevitAi.Core.SelfTest;

public enum SelfTestOutcome
{
    Passed,
    Failed,
    Skipped,
}

public sealed record SelfTestResult(string Area, string Name, SelfTestOutcome Outcome, string? Detail, double Milliseconds);

/// <summary>Formats the in-Revit self-test results as a Markdown report the user can send back (ADR-045).</summary>
public static class SelfTestReport
{
    public static string Summary(IReadOnlyList<SelfTestResult> results) =>
        $"{Count(results, SelfTestOutcome.Passed)} passed, {Count(results, SelfTestOutcome.Failed)} failed, " +
        $"{Count(results, SelfTestOutcome.Skipped)} skipped";

    public static string Format(
        IReadOnlyList<SelfTestResult> results,
        string revitVersion,
        string documentTitle,
        DateTimeOffset started,
        string addinVersion)
    {
        var text = new StringBuilder();
        text.AppendLine("# Revit AI self-test");
        text.AppendLine();
        text.AppendLine($"- Result: **{Summary(results)}**");
        text.AppendLine($"- Revit {revitVersion}, add-in {addinVersion}");
        text.AppendLine($"- Project: {documentTitle}");
        text.AppendLine($"- Started: {started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        text.AppendLine($"- Duration: {results.Sum(r => r.Milliseconds) / 1000:0.0} s");
        text.AppendLine("- Everything the self-test created was rolled back; the project was not changed.");
        text.AppendLine("- Not covered here (test manually): the panel UI, OpenAI answers, the dispatcher, Ctrl+Z after Apply.");

        List<SelfTestResult> failed = results.Where(r => r.Outcome == SelfTestOutcome.Failed).ToList();
        if (failed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Failures");
            text.AppendLine();
            foreach (SelfTestResult result in failed)
            {
                text.AppendLine($"- **{result.Area} / {result.Name}**: {OneLine(result.Detail)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## All checks");
        text.AppendLine();
        text.AppendLine("| Area | Check | Result | Detail | ms |");
        text.AppendLine("|---|---|---|---|---:|");
        foreach (SelfTestResult result in results)
        {
            string outcome = result.Outcome switch
            {
                SelfTestOutcome.Passed => "✅ passed",
                SelfTestOutcome.Failed => "❌ failed",
                _ => "⏭ skipped",
            };
            text.AppendLine($"| {Cell(result.Area)} | {Cell(result.Name)} | {outcome} | {Cell(result.Detail)} | {result.Milliseconds:0} |");
        }

        return text.ToString();
    }

    private static int Count(IReadOnlyList<SelfTestResult> results, SelfTestOutcome outcome) => results.Count(r => r.Outcome == outcome);

    private static string OneLine(string? text) => (text ?? "").ReplaceLineEndings(" ").Trim();

    private static string Cell(string? text) => OneLine(text).Replace("|", "\\|", StringComparison.Ordinal);
}

using System.Globalization;
using System.Text;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Query;

/// <summary>Formats a pipe-system check as Markdown for the ribbon button's saved report (ADR-048).</summary>
public static class PipeCheckReport
{
    public static string Summary(PipeSystemsReport report) =>
        $"{report.OpenEndCount} open pipe end(s), {report.PipesWithoutSystemCount} pipe(s) without a system, " +
        $"{report.SystemsNotWellConnected.Count} system(s) not well connected, {report.UnconnectedEquipmentCount} unconnected equipment connector(s)";

    public static bool HasProblems(PipeSystemsReport report) =>
        report.OpenEndCount + report.PipesWithoutSystemCount + report.SystemsNotWellConnected.Count + report.UnconnectedEquipmentCount > 0;

    public static string Format(PipeSystemsReport report, string documentTitle, DateTimeOffset when, PipeJointsReport? joints = null)
    {
        var text = new StringBuilder();
        text.AppendLine("# Pipe system check");
        text.AppendLine();
        text.AppendLine($"- Project: {documentTitle}");
        text.AppendLine($"- Checked: {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, {report.ElementsChecked} piping element(s)");
        text.AppendLine($"- Result: **{Summary(report)}**");
        if (report.Truncated)
        {
            text.AppendLine("- Lists below are shortened; the counts are complete.");
        }

        Section(text, "Open pipe ends", report.OpenEnds.Select(i => Line(i.Element, i.Location)));
        Section(text, "Pipes without a system", report.PipesWithoutSystem.Select(e => $"| {e.Id} | {Cell(e.Type)} | {Cell(e.Level)} | |"));
        Section(text, "Systems not well connected", report.SystemsNotWellConnected.Select(s => $"| {s.Id} | {Cell(s.Name)} | | |"));
        Section(text, "Unconnected equipment connectors", report.UnconnectedEquipment.Select(i => Line(i.Element, i.Location)));
        if (joints is not null)
        {
            Joints(text, joints);
        }

        return text.ToString();
    }

    public static string JointSummary(PipeJointsReport joints) =>
        $"{joints.ProposalCount} joint(s) can be added ({joints.ElbowCount} elbow(s), {joints.TeeCount} tee(s), " +
        $"{joints.MergeCount} merge(s)), {joints.UnclearCount} place(s) need a decision";

    private static void Joints(StringBuilder text, PipeJointsReport joints)
    {
        text.AppendLine();
        text.AppendLine($"## Joints that can be added ({joints.ProposalCount})");
        text.AppendLine();
        text.AppendLine("Open pipe ends next to another pipe where an elbow, tee or merge fits. Ask the assistant to " +
            "\"connect the pipes the check found\" to preview and apply them.");
        if (joints.DeferredCount > 0)
        {
            text.AppendLine($"{joints.DeferredCount} more joint(s) share a pipe with these; run the check again after applying.");
        }

        if (joints.Proposals.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("| Joint | Pipe 1 | Pipe 2 | Location (mm) |");
            text.AppendLine("|---|---:|---:|---|");
            foreach (PipeJointItem item in joints.Proposals)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {item.Kind} | {item.PipeId1} | {item.PipeId2} | {item.Location.X:0}, {item.Location.Y:0}, {item.Location.Z:0} |"));
            }
        }

        text.AppendLine();
        text.AppendLine($"## Open ends near a pipe that need a decision ({joints.UnclearCount})");
        text.AppendLine();
        if (joints.Unclear.Count == 0)
        {
            text.AppendLine("None.");
            return;
        }

        text.AppendLine("| Pipe | Nearest pipe | Why no joint was proposed | Location (mm) |");
        text.AppendLine("|---:|---:|---|---|");
        foreach (PipeJointProblem item in joints.Unclear)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {item.PipeId} | {item.NearestPipeId} | {Cell(item.Reason)} | {item.Location.X:0}, {item.Location.Y:0}, {item.Location.Z:0} |"));
        }
    }

    private static void Section(StringBuilder text, string title, IEnumerable<string> rows)
    {
        List<string> lines = rows.ToList();
        text.AppendLine();
        text.AppendLine($"## {title} ({lines.Count})");
        if (lines.Count == 0)
        {
            text.AppendLine();
            text.AppendLine("None.");
            return;
        }

        text.AppendLine();
        text.AppendLine("| Element ID | Type / name | Level | Location (mm) |");
        text.AppendLine("|---:|---|---|---|");
        lines.ForEach(line => text.AppendLine(line));
    }

    private static string Line(ElementSummary element, PointMm at) =>
        $"| {element.Id} | {Cell(element.Family is null ? element.Type : $"{element.Family}: {element.Type}")} | {Cell(element.Level)} | " +
        string.Create(CultureInfo.InvariantCulture, $"{at.X:0}, {at.Y:0}, {at.Z:0} |");

    private static string Cell(string? value) => (value ?? "").Replace("|", "\\|", StringComparison.Ordinal);
}

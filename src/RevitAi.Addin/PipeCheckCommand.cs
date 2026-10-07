using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Tools;
using RevitAi.Core.Query;
using RevitAi.Core.Tools;

namespace RevitAi.Addin;

/// <summary>
/// Ribbon button: pipe-system check without AI (ADR-048). Checks the selection if anything is selected, otherwise the whole
/// model; saves a Markdown report and can select the problem elements. Read-only.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class PipeCheckCommand : IExternalCommand
{
    private const string Title = "Revit AI pipe check";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (App.Services is not { } services || !services.Registry.TryGet("check_pipe_systems", out ITool tool) || tool is not RevitReadTool check)
        {
            message = "Revit AI did not start correctly; see the log.";
            return Result.Failed;
        }

        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
        if (uiDocument is null || uiDocument.Document.IsFamilyDocument)
        {
            TaskDialog.Show(Title, "Open a project first.");
            return Result.Cancelled;
        }

        ICollection<ElementId> selected = uiDocument.Selection.GetElementIds();
        JsonElement arguments = JsonSerializer.SerializeToElement(new
        {
            elementIds = selected.Count == 0 ? null : selected.Select(id => id.Value).ToArray(),
            levelId = (long?)null,
            limit = (long?)500,
        });

        var report = (PipeSystemsReport)check.ExecuteInContext(commandData.Application, arguments);
        DateTimeOffset now = DateTimeOffset.Now;
        string path = Path.Combine(services.LocalDataDir, $"pipe-check-{now:yyyyMMdd-HHmmss}.md");
        try
        {
            Directory.CreateDirectory(services.LocalDataDir);
            File.WriteAllText(path, PipeCheckReport.Format(report, uiDocument.Document.Title, now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Error("Could not write the pipe check report.", ex);
            path = "";
        }

        services.Log.Info($"Pipe check: {PipeCheckReport.Summary(report)}.");
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = PipeCheckReport.HasProblems(report) ? "Problems found" : "No pipe problems found",
            MainContent = $"Checked {(selected.Count == 0 ? "the whole model" : "the selection")}: {PipeCheckReport.Summary(report)}.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        List<ElementId> problems = report.OpenEnds.Select(i => i.Element.Id)
            .Concat(report.PipesWithoutSystem.Select(e => e.Id))
            .Concat(report.UnconnectedEquipment.Select(i => i.Element.Id))
            .Distinct()
            .Select(id => new ElementId(id))
            .ToList();
        if (problems.Count > 0)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Select the {problems.Count} problem element(s)");
        }

        if (path.Length > 0)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the report");
        }

        TaskDialogResult choice = dialog.Show();
        if (choice == TaskDialogResult.CommandLink1)
        {
            uiDocument.Selection.SetElementIds(problems);
        }
        else if (choice == TaskDialogResult.CommandLink2)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }

        return Result.Succeeded;
    }
}

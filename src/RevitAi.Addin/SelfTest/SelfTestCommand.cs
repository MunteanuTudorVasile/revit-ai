using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Core.SelfTest;

namespace RevitAi.Addin.SelfTest;

/// <summary>
/// Ribbon command that runs the automatic self-test on the open project (ADR-045). Everything happens inside one
/// transaction group that is always rolled back, so the project is left exactly as it was.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class SelfTestCommand : IExternalCommand
{
    private const string Title = "Revit AI self-test";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (App.Services is not { } services)
        {
            message = "Revit AI did not start correctly; see the log.";
            return Result.Failed;
        }

        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
        if (uiDocument is null || uiDocument.Document.IsFamilyDocument || uiDocument.Document.IsReadOnly)
        {
            TaskDialog.Show(Title, "Open a project (not a family, not read-only) first.");
            return Result.Cancelled;
        }

        Document document = uiDocument.Document;
        if (document.IsModifiable)
        {
            TaskDialog.Show(Title, "Finish the current edit (e.g. sketch mode) first.");
            return Result.Cancelled;
        }

        var confirm = new TaskDialog(Title)
        {
            MainInstruction = "Run the automatic checks on this project?",
            MainContent = "About 50 checks create walls, rooms, views, sheets and more about 300 m away from the model, verify them, "
                          + "and then undo everything. The project is not changed. Using a copy of a project is still recommended.\n\n"
                          + "This takes a few seconds to a minute.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.Yes,
        };
        if (confirm.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        DateTimeOffset started = DateTimeOffset.Now;
        services.Log.Info($"Self-test started on '{document.Title}'.");
        var runner = new SelfTestRunner(commandData.Application, services.Registry, services.Executor);

        using (var group = new TransactionGroup(document, Title))
        {
            group.Start();
            try
            {
                new SelfTestScenarios(runner).RunAll();
            }
            finally
            {
                if (group.GetStatus() == TransactionStatus.Started)
                {
                    group.RollBack();
                }
            }
        }

        string report = SelfTestReport.Format(
            runner.Results,
            commandData.Application.Application.VersionNumber,
            document.Title,
            started,
            typeof(App).Assembly.GetName().Version?.ToString() ?? "?");
        string path = Path.Combine(services.LocalDataDir, $"self-test-{started:yyyyMMdd-HHmmss}.md");
        try
        {
            Directory.CreateDirectory(services.LocalDataDir);
            File.WriteAllText(path, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Error("Could not write the self-test report.", ex);
            path = "";
        }

        string summary = SelfTestReport.Summary(runner.Results);
        services.Log.Info($"Self-test finished: {summary}. Report: {path}");
        ShowResult(summary, path, runner.Results.Count(r => r.Outcome == SelfTestOutcome.Failed));
        return Result.Succeeded;
    }

    private static void ShowResult(string summary, string path, int failed)
    {
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = failed == 0 ? $"All checks passed ({summary})." : $"{failed} check(s) failed ({summary}).",
            MainContent = path.Length > 0
                ? $"The report is saved at:\n{path}\n\nSend this file to the developer. The project was not changed."
                : "The report could not be saved; see the log. The project was not changed.",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (path.Length > 0)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the report");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open its folder");
        }

        TaskDialogResult choice = dialog.Show();
        if (choice == TaskDialogResult.CommandLink1)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No app is associated with .md files on many PCs.
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        else if (choice == TaskDialogResult.CommandLink2)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }
}

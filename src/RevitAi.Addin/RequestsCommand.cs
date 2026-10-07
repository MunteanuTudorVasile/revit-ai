using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Core.Ai;

namespace RevitAi.Addin;

/// <summary>
/// Ribbon button (ADR-051): summarises the requests the assistant could not do yet, grouped by area and missing capability,
/// and saves the summary as Markdown. Reads only the local request log; does not touch the model.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class RequestsCommand : IExternalCommand
{
    private const string Title = "Revit AI requests";
    private const int ShownInDialog = 5;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (App.Services is not { } services)
        {
            message = "Revit AI did not start correctly; see the log.";
            return Result.Failed;
        }

        IReadOnlyList<UnmetRequest> requests = UnmetRequestFile.Read(Path.Combine(services.LocalDataDir, UnmetRequestLog.FileName));
        if (requests.Count == 0)
        {
            TaskDialog.Show(Title, "No requests recorded yet. When the assistant has to say something is not available, it is noted here.");
            return Result.Succeeded;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        string path = Path.Combine(services.LocalDataDir, $"requests-summary-{now:yyyyMMdd-HHmmss}.md");
        try
        {
            File.WriteAllText(path, UnmetRequestSummary.Format(requests, now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Error("Could not write the requests summary.", ex);
            path = "";
        }

        IReadOnlyList<UnmetCapability> top = UnmetRequestSummary.Group(requests);
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = $"{requests.Count} request(s) the assistant could not do yet",
            MainContent = "Most asked:\n" + string.Join("\n", top.Take(ShownInDialog).Select(c =>
                $"• {c.Capability} ({UnmetRequestSummary.AreaName(c.Area)}): {c.Count}×")),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        if (path.Length > 0)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the full summary");
        }

        if (dialog.Show() == TaskDialogResult.CommandLink1)
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

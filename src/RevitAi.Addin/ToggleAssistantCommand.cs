using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAi.Addin;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ToggleAssistantCommand : IExternalCommand, IExternalCommandAvailability
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        DockablePane pane = commandData.Application.GetDockablePane(App.PaneId);
        if (pane.IsShown())
        {
            pane.Hide();
        }
        else
        {
            pane.Show();
        }

        return Result.Succeeded;
    }

    // The panel can be toggled even when no document is open.
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

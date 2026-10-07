using Autodesk.Revit.UI;

namespace RevitAi.Addin.UI;

public sealed class AssistantPaneProvider : IDockablePaneProvider
{
    private readonly AssistantPane _pane;

    public AssistantPaneProvider(AssistantPane pane)
    {
        _pane = pane;
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _pane;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
    }
}

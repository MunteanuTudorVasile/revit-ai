using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Tools;
using RevitAi.Core.Context;

namespace RevitAi.Addin.Context;

/// <summary>Reads the current <see cref="ModelContext"/>. Must run inside Revit's API context.</summary>
public static class ContextReader
{
    public static ModelContext Read(UIApplication app) => Read(app.ActiveUIDocument?.Document, app.ActiveUIDocument);

    public static ModelContext Read(Document? document) =>
        Read(document, document is null ? null : new UIDocument(document));

    private static ModelContext Read(Document? document, UIDocument? uiDocument)
    {
        if (document is null || uiDocument is null || document.IsFamilyDocument)
        {
            return ModelContext.NoDocument;
        }

        View? view = document.ActiveView;
        ICollection<ElementId> selected = uiDocument.Selection.GetElementIds();
        return new ModelContext(
            DocumentTitle: document.Title,
            ViewName: view?.Name,
            ViewType: view?.ViewType.ToString(),
            LevelName: view?.GenLevel?.Name,
            SelectionCount: selected.Count,
            SelectionPreview: selected
                .Take(ModelContext.MaxSelectionPreview)
                .Select(document.GetElement)
                .Where(element => element is not null)
                .Select(RevitRead.Summarize)
                .ToList(),
            ViewId: view?.Id.Value);
    }
}

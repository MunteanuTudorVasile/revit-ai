using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Localization;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

/// <param name="ElementId">
/// The element this operation created or changed; later operations can reference it as <c>$opN.elementId</c>.
/// Null when there is no single main element (e.g. several tags).
/// </param>
/// <param name="OtherIds">Further elements created or changed, for history and auditing.</param>
public sealed record OperationResult(long? ElementId, string Outcome, IReadOnlyList<long>? OtherIds = null);

/// <summary>
/// A tool that proposes a model change (ADR-024). <see cref="ValidateAsync"/> checks it without changing anything;
/// <see cref="Apply"/> is called later by <c>PlanExecutor</c> inside an open transaction.
/// </summary>
public abstract class RevitWriteTool : IWriteTool
{
    private readonly RevitDispatcher _dispatcher;
    private readonly TextSource _text;
    private JsonElement? _schema;

    protected RevitWriteTool(RevitDispatcher dispatcher, TextSource text)
    {
        _dispatcher = dispatcher;
        _text = text;
    }

    /// <summary>Texts in the current panel language, for plan summaries, outcomes and failure reasons.</summary>
    protected UiText T => _text.Current;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string ProgressLabel { get; }

    public JsonElement InputSchema => _schema ??= ToolSchema.Parse(SchemaJson);

    public virtual RiskLevel Risk => RiskLevel.SafeModification;

    protected abstract string SchemaJson { get; }

    public Task<string> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(app => Validate(RevitRead.RequireDocument(app), arguments), cancellationToken);

    /// <summary>
    /// Runs inside Revit's API context, outside any transaction. Must not modify the model.
    /// Returns the one-line plan summary or throws <see cref="ToolException"/>.
    /// </summary>
    protected abstract string Validate(Document document, JsonElement arguments);

    /// <summary>For the in-Revit self-test, which already runs in API context and bypasses the dispatcher.</summary>
    internal string ValidateInContext(Document document, JsonElement arguments) => Validate(document, arguments);

    /// <summary>
    /// Runs inside an open transaction with all plan references already resolved to IDs.
    /// Throws <see cref="ToolException"/> (or lets Revit throw) to fail the step; the plan is then rolled back.
    /// </summary>
    public abstract OperationResult Apply(Document document, JsonElement arguments);
}

/// <summary>Argument parsing and model lookups shared by the write tools.</summary>
internal static class WriteArgs
{
    /// <summary>The ID, or null when the argument is a plan reference that can only be checked when the plan runs.</summary>
    public static long? IdOrReference(UiText t, JsonElement arguments, string name)
    {
        JsonElement value = arguments.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt64();
        }

        return PlanReferences.TryParse(value.GetString(), out _)
            ? null
            : throw new ToolException(t.Format("Tool.IdOrReference", name));
    }

    /// <summary>For <c>Apply</c>, where references have been resolved.</summary>
    public static long Id(UiText t, JsonElement arguments, string name) =>
        IdOrReference(t, arguments, name) ?? throw new ToolException(t.Format("Tool.UnresolvedReference", name));

    public static Point2 Point(JsonElement arguments, string name)
    {
        JsonElement point = arguments.GetProperty(name);
        return new Point2(point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble());
    }

    public static double? OptionalNumber(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public static XYZ ToXyz(Point2 point) => new(RevitRead.Feet(point.X), RevitRead.Feet(point.Y), 0);

    public static Level Level(UiText t, Document document, long id) =>
        document.GetElement(new ElementId(id)) as Level ?? throw new ToolException(t.Format("Tool.NotALevel", id));

    public static Wall Wall(UiText t, Document document, long id) =>
        document.GetElement(new ElementId(id)) as Wall ?? throw new ToolException(t.Format("Tool.NotAWall", id));

    public static Line WallLine(UiText t, Wall wall) =>
        (wall.Location as LocationCurve)?.Curve as Line
        ?? throw new ToolException(t.Format("Tool.NotStraight", wall.Id.Value));

    /// <summary>The given type, or the project's default when <paramref name="typeId"/> is null.</summary>
    /// <param name="categoryKey">Text key naming the category in the plural, e.g. "Tool.CatWalls".</param>
    public static TType TypeOrDefault<TType>(UiText t, Document document, long? typeId, ElementId defaultId, string categoryKey)
        where TType : ElementType
    {
        if (typeId is null)
        {
            return document.GetElement(defaultId) as TType
                ?? throw new ToolException(t.Format("Tool.NoDefaultType", t[categoryKey]));
        }

        return document.GetElement(new ElementId(typeId.Value)) as TType
            ?? throw new ToolException(t.Format("Tool.NotAType", typeId, t[categoryKey]));
    }

    public static FamilySymbol FamilyType(UiText t, Document document, long? typeId, BuiltInCategory category, string categoryKey)
    {
        ElementId defaultId = document.GetDefaultFamilyTypeId(new ElementId(category));
        FamilySymbol symbol = TypeOrDefault<FamilySymbol>(t, document, typeId, defaultId, categoryKey);
        return symbol.Category?.Id.Value == (long)category
            ? symbol
            : throw new ToolException(t.Format("Tool.WrongCategoryType", symbol.Id.Value, $"{symbol.FamilyName}: {symbol.Name}", t[categoryKey]));
    }

    public static string Mm(double value) => $"{value:0} mm";
}

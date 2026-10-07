using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Geometry;
using RevitAi.Core.Planning;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

/// <param name="ElementId">The element this operation created or changed; later operations can reference it.</param>
public sealed record OperationResult(long ElementId, string Outcome);

/// <summary>
/// A tool that proposes a model change (ADR-024). <see cref="ValidateAsync"/> checks it without changing anything;
/// <see cref="Apply"/> is called later by <c>PlanExecutor</c> inside an open transaction.
/// </summary>
public abstract class RevitWriteTool : IWriteTool
{
    private readonly RevitDispatcher _dispatcher;
    private JsonElement? _schema;

    protected RevitWriteTool(RevitDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

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
    public static long? IdOrReference(JsonElement arguments, string name)
    {
        JsonElement value = arguments.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt64();
        }

        return PlanReferences.TryParse(value.GetString(), out _)
            ? null
            : throw new ToolException($"{name} must be an element ID or a reference like $op1.elementId.");
    }

    /// <summary>For <c>Apply</c>, where references have been resolved.</summary>
    public static long Id(JsonElement arguments, string name) =>
        IdOrReference(arguments, name) ?? throw new ToolException($"{name} still contains an unresolved reference.");

    public static Point2 Point(JsonElement arguments, string name)
    {
        JsonElement point = arguments.GetProperty(name);
        return new Point2(point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble());
    }

    public static double? OptionalNumber(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public static XYZ ToXyz(Point2 point) => new(RevitRead.Feet(point.X), RevitRead.Feet(point.Y), 0);

    public static Level Level(Document document, long id) =>
        document.GetElement(new ElementId(id)) as Level ?? throw new ToolException($"Element {id} is not a level.");

    public static Wall Wall(Document document, long id) =>
        document.GetElement(new ElementId(id)) as Wall ?? throw new ToolException($"Element {id} is not a wall.");

    public static Line WallLine(Wall wall) =>
        (wall.Location as LocationCurve)?.Curve as Line
        ?? throw new ToolException($"Wall {wall.Id.Value} is not a straight wall; only straight walls are supported.");

    /// <summary>The given type, or the project's default when <paramref name="typeId"/> is null.</summary>
    public static T TypeOrDefault<T>(Document document, long? typeId, ElementId defaultId, string what)
        where T : ElementType
    {
        if (typeId is null)
        {
            return document.GetElement(defaultId) as T
                ?? throw new ToolException($"The project has no default {what}. Pass the typeId of an existing {what.Replace(" type", "")}.");
        }

        return document.GetElement(new ElementId(typeId.Value)) as T
            ?? throw new ToolException($"Element {typeId} is not a {what}.");
    }

    public static FamilySymbol FamilyType(Document document, long? typeId, BuiltInCategory category, string what)
    {
        ElementId defaultId = document.GetDefaultFamilyTypeId(new ElementId(category));
        FamilySymbol symbol = TypeOrDefault<FamilySymbol>(document, typeId, defaultId, what);
        return symbol.Category?.Id.Value == (long)category
            ? symbol
            : throw new ToolException($"Type {symbol.Id.Value} ({symbol.FamilyName}: {symbol.Name}) is not a {what}.");
    }

    public static string Mm(double value) => $"{value:0} mm";
}

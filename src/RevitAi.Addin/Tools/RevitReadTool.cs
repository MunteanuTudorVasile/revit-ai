using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

/// <summary>A read-only tool whose work runs inside Revit's API context through the dispatcher.</summary>
public abstract class RevitReadTool : ITool
{
    protected const string NoArguments = """{ "type": "object", "properties": {}, "required": [], "additionalProperties": false }""";

    private readonly RevitDispatcher _dispatcher;
    private JsonElement? _schema;

    protected RevitReadTool(RevitDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string ProgressLabel { get; }

    public JsonElement InputSchema => _schema ??= ToolSchema.Parse(SchemaJson);

    public RiskLevel Risk => RiskLevel.ReadOnly;

    protected abstract string SchemaJson { get; }

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(app => Execute(app, arguments), cancellationToken);

    /// <summary>Runs inside Revit's API context. Must not modify the model.</summary>
    protected abstract object Execute(UIApplication app, JsonElement arguments);
}

/// <summary>Shared Revit → result-contract conversions. Units per ADR-026: mm, m².</summary>
internal static class RevitRead
{
    public static UIDocument RequireUiDocument(UIApplication app) =>
        app.ActiveUIDocument ?? throw new ToolException("No project is open in Revit.");

    public static Document RequireDocument(UIApplication app) => RequireUiDocument(app).Document;

    public static Element RequireElement(Document document, long id) =>
        document.GetElement(new ElementId(id)) ?? throw new ToolException($"No element with ID {id} exists in this project.");

    public static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 1);

    public static double M2(double squareFeet) => Math.Round(UnitUtils.ConvertFromInternalUnits(squareFeet, UnitTypeId.SquareMeters), 3);

    public static PointMm Point(XYZ point) => new(Mm(point.X), Mm(point.Y), Mm(point.Z));

    public static ElementSummary Summarize(Element element)
    {
        Document document = element.Document;
        var type = document.GetElement(element.GetTypeId()) as ElementType;
        string? level = element.LevelId == ElementId.InvalidElementId ? null : document.GetElement(element.LevelId)?.Name;
        return new ElementSummary(element.Id.Value, element.Category?.Name, type?.FamilyName, type?.Name, element.Name, level);
    }

    public static long? OptionalLong(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    public static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

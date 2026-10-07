using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Standards;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

/// <summary>Type lookups shared by the type tools: default type, usage counts and standard rank.</summary>
internal static class TypeCatalog
{
    public static List<FamilyTypeInfo> Load(Document document, Category category, IReadOnlyList<string> preferred)
    {
        Dictionary<long, int> usage = new FilteredElementCollector(document)
            .OfCategoryId(category.Id)
            .WhereElementIsNotElementType()
            .GroupBy(e => e.GetTypeId().Value)
            .ToDictionary(g => g.Key, g => g.Count());

        long defaultId = DefaultTypeId(document, category).Value;

        return new FilteredElementCollector(document)
            .OfCategoryId(category.Id)
            .WhereElementIsElementType()
            .Cast<ElementType>()
            .Select(type => new FamilyTypeInfo(
                type.Id.Value,
                RevitRead.NullIfEmpty(type.FamilyName),
                type.Name,
                type.Id.Value == defaultId,
                Rank(preferred, type),
                usage.GetValueOrDefault(type.Id.Value)))
            .OrderBy(t => t.PreferredRank ?? int.MaxValue)
            .ThenByDescending(t => t.IsDefault)
            .ThenByDescending(t => t.InstanceCount)
            .ThenBy(t => t.Family)
            .ThenBy(t => t.Type)
            .ToList();
    }

    private static int? Rank(IReadOnlyList<string> preferred, ElementType type)
    {
        for (int i = 0; i < preferred.Count; i++)
        {
            if (ProjectStandards.Matches(preferred[i], type.FamilyName, type.Name))
            {
                return i + 1;
            }
        }

        return null;
    }

    private static ElementId DefaultTypeId(Document document, Category category)
    {
        var builtIn = (BuiltInCategory)category.Id.Value;
        ElementTypeGroup? group = builtIn switch
        {
            BuiltInCategory.OST_Walls => ElementTypeGroup.WallType,
            BuiltInCategory.OST_Floors => ElementTypeGroup.FloorType,
            BuiltInCategory.OST_Roofs => ElementTypeGroup.RoofType,
            BuiltInCategory.OST_Ceilings => ElementTypeGroup.CeilingType,
            _ => null,
        };

        if (group is not null)
        {
            return document.GetDefaultElementTypeId(group.Value);
        }

        try
        {
            return document.GetDefaultFamilyTypeId(category.Id);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            return ElementId.InvalidElementId;
        }
    }
}

public sealed class FindFamilyTypesTool(RevitDispatcher dispatcher, string standardsPath) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    public override string Name => "find_family_types";

    public override string Description =>
        "Lists the types available in the project for one category (e.g. Walls, Doors, Windows, Floors): typeId, family, type, " +
        "whether it is the project default, its rank in the project standards (if any) and how many elements use it. " +
        $"Sorted by standard rank, then default, then usage. Returns up to 'limit' types (default {DefaultLimit}, max {MaxLimit}).";

    public override string ProgressLabel => "Looking up available types…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name as shown in Revit, e.g. Walls, Doors, Windows, Floors." },
            "nameContains": { "type": ["string", "null"], "description": "Case-insensitive text the family or type name must contain. Null for all." },
            "limit": { "type": ["integer", "null"], "description": "Maximum types to return. Null for the default." }
          },
          "required": ["category", "nameContains", "limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        Category category = RevitRead.RequireCategory(document, arguments.GetProperty("category").GetString()!);
        string? nameContains = RevitRead.OptionalString(arguments, "nameContains");
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);

        IReadOnlyList<string> preferred = ProjectStandards.Load(standardsPath, out _).PreferredFor(document.Title, category.Name);
        List<FamilyTypeInfo> types = TypeCatalog.Load(document, category, preferred)
            .Where(t => nameContains is null
                        || t.Type.Contains(nameContains, StringComparison.OrdinalIgnoreCase)
                        || (t.Family?.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        return new FamilyTypesResult(category.Name, types.Count, types.Count > limit, types.Take(limit).ToList());
    }
}

public sealed class GetProjectStandardTypesTool(RevitDispatcher dispatcher, string standardsPath) : RevitReadTool(dispatcher)
{
    public override string Name => "get_project_standard_types";

    public override string Description =>
        "Returns the project's preferred types for one category, in order of preference, as configured by the user in standards.json. " +
        "Use these first when creating elements. 'configured' is false when no standards exist for the category.";

    public override string ProgressLabel => "Reading the project's standard types…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category name as shown in Revit, e.g. Walls, Doors, Windows, Floors." }
          },
          "required": ["category"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        Category category = RevitRead.RequireCategory(document, arguments.GetProperty("category").GetString()!);
        ProjectStandards standards = ProjectStandards.Load(standardsPath, out string? problem);
        IReadOnlyList<string> preferred = standards.PreferredFor(document.Title, category.Name);

        if (preferred.Count == 0)
        {
            return new StandardTypesResult(category.Name, false, [], [],
                problem ?? $"No standard {category.Name} types are configured. Fall back to similar existing elements or find_family_types.");
        }

        List<FamilyTypeInfo> types = TypeCatalog.Load(document, category, preferred);
        List<FamilyTypeInfo> matched = types.Where(t => t.PreferredRank is not null).ToList();
        List<string> notFound = preferred
            .Where(entry => !types.Any(t => ProjectStandards.Matches(entry, t.Family, t.Type)))
            .ToList();

        return new StandardTypesResult(category.Name, true, matched, notFound,
            notFound.Count == 0 ? null : "Some standard types are not loaded in this project; they cannot be used.");
    }
}

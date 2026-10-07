using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Localization;
using RevitAi.Core.Standards;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

// Phase 6: company/project standards (ADR-038). Checks are read-only; fixes go through plan → preview → apply.

public sealed class GetProjectStandardsTool(RevitDispatcher dispatcher, string standardsPath) : RevitReadTool(dispatcher)
{
    public override string Name => "get_project_standards";

    public override string Description =>
        "Returns the standards that apply to this project (standards.json): allowed room names, sheet number pattern, " +
        "view name patterns and view templates per view type, required parameters per category, and standard types per category. " +
        "Follow them when naming or creating things.";

    public override string ProgressLabel => "Reading the project standards…";

    protected override string SchemaJson => NoArguments;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        ProjectStandards standards = ProjectStandards.Load(standardsPath, out string? loadProblem);
        StandardsRules rules = standards.RulesFor(document.Title, out IReadOnlyList<string> ruleProblems);

        return new StandardsSummary(
            rules.RoomNames,
            rules.SheetNumberPattern,
            rules.ViewNamePatterns,
            rules.RequiredParameters,
            rules.ViewTemplates,
            standards.TypeCategories(document.Title).ToDictionary(c => c, c => standards.PreferredFor(document.Title, c)),
            [.. (loadProblem is null ? [] : new[] { loadProblem }), .. ruleProblems]);
    }
}

public sealed class CheckStandardsTool(RevitDispatcher dispatcher, string standardsPath) : RevitReadTool(dispatcher)
{
    private const int DefaultLimit = 20;

    public override string Name => "check_standards";

    public override string Description =>
        "Checks the model against every configured standard (room names, sheet numbers, view names, view templates, " +
        "required parameters, standard types) and returns the violations per rule. 'limit' caps the examples per rule (default 20).";

    public override string ProgressLabel => "Checking the company standards…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "limit": { "type": ["integer", "null"], "description": "Maximum violations listed per rule. Null for 20." }
          },
          "required": ["limit"],
          "additionalProperties": false
        }
        """;

    protected override object Execute(UIApplication app, JsonElement arguments)
    {
        Document document = RevitRead.RequireDocument(app);
        int limit = (int)Math.Clamp(RevitRead.OptionalLong(arguments, "limit") ?? DefaultLimit, 1, Qa.MaxLimit);
        ProjectStandards standards = ProjectStandards.Load(standardsPath, out string? loadProblem);
        StandardsRules rules = standards.RulesFor(document.Title, out IReadOnlyList<string> ruleProblems);
        var problems = new List<string>(ruleProblems);
        if (loadProblem is not null)
        {
            problems.Add(loadProblem);
        }

        var results = new List<RuleResult>();
        void Add(string rule, string description, List<QaElement> violations) =>
            results.Add(new RuleResult(rule, description, violations.Count, violations.Count > limit, violations.Take(limit).ToList()));

        if (rules.RoomNames is { Count: > 0 })
        {
            Add("roomNames", "Room names must be in the allowed list.", new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .Select(r => (Room: r, Name: RevitRead.RoomName(r)))
                .Where(x => !ProjectStandards.IsAllowedRoomName(rules, x.Name))
                .Select(x => new QaElement(RevitRead.Summarize(x.Room), $"Name '{x.Name}' is not in the allowed list"))
                .ToList());
        }

        if (rules.SheetNumberPattern is { } sheetPattern)
        {
            Add("sheetNumberPattern", $"Sheet numbers must match {sheetPattern}.", new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && Violates(sheetPattern, s.SheetNumber, "sheetNumberPattern", problems))
                .Select(s => new QaElement(RevitRead.Summarize(s), $"Sheet number '{s.SheetNumber}'"))
                .ToList());
        }

        foreach ((string typeName, string pattern) in rules.ViewNamePatterns ?? [])
        {
            if (ParseViewType(typeName, problems) is { } viewType)
            {
                Add($"viewNamePatterns.{typeName}", $"{typeName} view names must match {pattern}.", Views(document, viewType)
                    .Where(v => Violates(pattern, v.Name, $"viewNamePatterns.{typeName}", problems))
                    .Select(v => new QaElement(RevitRead.Summarize(v), $"View name '{v.Name}'"))
                    .ToList());
            }
        }

        foreach ((string typeName, string templateName) in rules.ViewTemplates ?? [])
        {
            if (ParseViewType(typeName, problems) is { } viewType)
            {
                Add($"viewTemplates.{typeName}", $"{typeName} views must use the view template '{templateName}'.", Views(document, viewType)
                    .Select(v => (View: v, Template: document.GetElement(v.ViewTemplateId)?.Name))
                    .Where(x => !string.Equals(x.Template, templateName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new QaElement(RevitRead.Summarize(x.View), $"Template is '{x.Template ?? "none"}'"))
                    .ToList());
            }
        }

        foreach ((string categoryName, List<string> parameterNames) in rules.RequiredParameters ?? [])
        {
            if (FindCategory(document, categoryName, problems) is { } category)
            {
                foreach (string parameterName in parameterNames)
                {
                    Add($"requiredParameters.{categoryName}.{parameterName}", $"{categoryName} must have a value for '{parameterName}'.",
                        new FilteredElementCollector(document)
                            .OfCategoryId(category.Id)
                            .WhereElementIsNotElementType()
                            .Select(e => (Element: e, Reason: Qa.MissingParameterReason(document, e, parameterName)))
                            .Where(x => x.Reason is not null)
                            .Select(x => new QaElement(RevitRead.Summarize(x.Element), x.Reason))
                            .ToList());
                }
            }
        }

        foreach (string categoryName in standards.TypeCategories(document.Title))
        {
            if (FindCategory(document, categoryName, problems) is { } category)
            {
                Add($"standardTypes.{categoryName}", $"{categoryName} must use the standard types.",
                    Qa.NonStandardTypes(document, category, standards.PreferredFor(document.Title, categoryName)));
            }
        }

        bool configured = !rules.IsEmpty || standards.TypeCategories(document.Title).Count > 0;
        if (!configured)
        {
            problems.Add("No standards are configured in standards.json for this project, so nothing was checked.");
        }

        return new StandardsReport(configured, results, problems.Distinct().ToList());
    }

    /// <summary>True when the value breaks the pattern. A pattern too slow to evaluate is reported, not treated as passing.</summary>
    private static bool Violates(string pattern, string? value, string rule, List<string> problems)
    {
        bool? matches = ProjectStandards.MatchesPattern(pattern, value);
        if (matches is null)
        {
            problems.Add($"{rule} '{pattern}' took too long to evaluate for some names; those were not checked. Simplify the pattern.");
        }

        return matches == false;
    }

    private static IEnumerable<View> Views(Document document, ViewType viewType) =>
        new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.ViewType == viewType);

    private static ViewType? ParseViewType(string name, List<string> problems)
    {
        if (Enum.TryParse(name, ignoreCase: true, out ViewType viewType))
        {
            return viewType;
        }

        problems.Add($"'{name}' in standards.json is not a Revit view type (e.g. FloorPlan, Section); that rule was skipped.");
        return null;
    }

    private static Category? FindCategory(Document document, string name, List<string> problems)
    {
        try
        {
            return RevitRead.RequireCategory(document, name);
        }
        catch (ToolException)
        {
            problems.Add($"'{name}' in standards.json is not a category in this project; that rule was skipped.");
            return null;
        }
    }
}

public sealed class SetParametersTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int MaxElements = 200;
    private const int MaxListed = 5;

    public override string Name => "set_parameters";

    public override string Description =>
        "Proposes setting one instance parameter to the same value on one or more elements. Also used for renaming: rooms " +
        "'Name' and 'Number', views 'View Name', sheets 'Sheet Number' and 'Sheet Name'. Numbers: lengths in mm, areas in m², " +
        "angles in degrees, volumes in m³; yes/no parameters take true/false. Type parameters and element references are refused. " +
        $"At most {MaxElements} elements.";

    public override string ProgressLabel => "Checking the parameter values…";

    // Bulk changes: always preview first.
    public override RiskLevel Risk => RiskLevel.LargeModification;

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "elementIds": { "type": "array", "items": { "type": ["integer", "string"] }, "description": "Elements to change, or $opN.elementId references." },
            "parameterName": { "type": "string", "description": "Parameter name as shown in the Properties palette." },
            "value": { "type": ["string", "number", "boolean"], "description": "New value." }
          },
          "required": ["elementIds", "parameterName", "value"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        (string name, JsonElement value, List<JsonElement> ids) = Inputs(arguments);
        var described = new List<string>();
        foreach (JsonElement id in ids)
        {
            if (id.ValueKind == JsonValueKind.Number)
            {
                Element element = RevitRead.RequireElement(document, id.GetInt64());
                Writable(document, element, name, value);
                described.Add($"{element.Category?.Name} {element.Id.Value}");
            }
            else
            {
                described.Add(id.GetString()!);
            }
        }

        string list = string.Join(", ", described.Take(MaxListed)) + (described.Count > MaxListed ? ", …" : "");
        return T.Format("Tool.SetSummary", name, Display(value), ids.Count, list);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        (string name, JsonElement value, List<JsonElement> ids) = Inputs(arguments);
        var changed = new List<long>();
        foreach (JsonElement id in ids)
        {
            Element element = RevitRead.RequireElement(document, id.GetInt64());
            Parameter parameter = Writable(document, element, name, value);
            bool ok = parameter.StorageType switch
            {
                StorageType.String => parameter.Set(AsText(value)),
                StorageType.Integer => parameter.Set(value.ValueKind == JsonValueKind.Number ? value.GetInt32() : value.GetBoolean() ? 1 : 0),
                StorageType.Double => parameter.Set(ToInternal(element, parameter, value.GetDouble())),
                _ => false,
            };

            if (!ok)
            {
                throw new ToolException(T.Format("Tool.ParamRejected", element.Id.Value, name));
            }

            changed.Add(element.Id.Value);
        }

        return new OperationResult(changed[0], T.Format("Tool.SetDone", name, Display(value), changed.Count), changed.Skip(1).ToList());
    }

    private (string Name, JsonElement Value, List<JsonElement> Ids) Inputs(JsonElement arguments)
    {
        List<JsonElement> ids = arguments.GetProperty("elementIds").EnumerateArray().ToList();
        return ids.Count == 0 ? throw new ToolException(T["Tool.MoveNeedsElements"])
            : ids.Count > MaxElements ? throw new ToolException(T.Format("Tool.SetTooMany", MaxElements))
            : (arguments.GetProperty("parameterName").GetString()!, arguments.GetProperty("value"), ids);
    }

    /// <summary>The instance parameter, if it exists, is writable and accepts this kind of value.</summary>
    private Parameter Writable(Document document, Element element, string name, JsonElement value)
    {
        long id = element.Id.Value;
        Parameter parameter = element.LookupParameter(name)
            ?? throw new ToolException(document.GetElement(element.GetTypeId())?.LookupParameter(name) is not null
                ? T.Format("Tool.ParamOnType", id, name)
                : T.Format("Tool.ParamMissing", id, name));

        if (parameter.IsReadOnly)
        {
            throw new ToolException(T.Format("Tool.ParamReadOnly", id, name));
        }

        if (parameter.StorageType == StorageType.Double)
        {
            ToolUnit(element, parameter); // throws for units the tools can't express
        }

        bool accepted = parameter.StorageType switch
        {
            StorageType.String => true,
            StorageType.Integer => value.ValueKind is JsonValueKind.True or JsonValueKind.False
                                   || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)),
            StorageType.Double => value.ValueKind == JsonValueKind.Number,
            StorageType.ElementId => throw new ToolException(T.Format("Tool.ParamElementId", id, name)),
            _ => false,
        };

        return accepted ? parameter : throw new ToolException(T.Format("Tool.ParamWrongValue", id, name, T[parameter.StorageType switch
        {
            StorageType.Integer => "Tool.ValueWholeNumber",
            StorageType.Double => "Tool.ValueNumber",
            _ => "Tool.ValueText",
        }]));
    }

    private static readonly ForgeTypeId[] ToolUnits =
        [UnitTypeId.Millimeters, UnitTypeId.SquareMeters, UnitTypeId.Degrees, UnitTypeId.CubicMeters];

    private double ToInternal(Element element, Parameter parameter, double value) =>
        ToolUnit(element, parameter) is { } unit ? UnitUtils.ConvertToInternalUnits(value, unit) : value;

    /// <summary>
    /// The tool unit (mm, m², degrees, m³) of a measurable parameter, or null for plain numbers. Any measurable spec that
    /// accepts one of these units is converted (lengths, pipe/duct sizes, areas, …); other units (e.g. currency, flow)
    /// are refused rather than stored in Revit's internal units, which would silently mean feet.
    /// </summary>
    private ForgeTypeId? ToolUnit(Element element, Parameter parameter)
    {
        ForgeTypeId spec = parameter.Definition.GetDataType();
        if (spec == SpecTypeId.Number || !UnitUtils.IsMeasurableSpec(spec))
        {
            return null;
        }

        IList<ForgeTypeId> valid = UnitUtils.GetValidUnits(spec);
        return ToolUnits.FirstOrDefault(valid.Contains)
            ?? throw new ToolException(T.Format("Tool.ParamUnits", element.Id.Value, parameter.Definition.Name));
    }

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        _ => value.GetDouble().ToString(CultureInfo.InvariantCulture),
    };

    private static string Display(JsonElement value) => value.ValueKind == JsonValueKind.String ? $"\"{value.GetString()}\"" : AsText(value);
}

public sealed class ApplyViewTemplateTool(RevitDispatcher dispatcher, TextSource text) : RevitWriteTool(dispatcher, text)
{
    private const int MaxListed = 5;

    public override string Name => "apply_view_template";

    public override string Description =>
        "Proposes assigning a view template to views (find_views with templatesOnly true lists templates). " +
        "Views may be $opN.elementId references to views created earlier in this plan.";

    public override string ProgressLabel => "Checking the view template…";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "viewIds": { "type": "array", "items": { "type": ["integer", "string"] }, "description": "Views to change, or $opN.elementId references." },
            "templateId": { "type": "integer", "description": "View template ID." }
          },
          "required": ["viewIds", "templateId"],
          "additionalProperties": false
        }
        """;

    protected override string Validate(Document document, JsonElement arguments)
    {
        View template = Template(document, arguments);
        List<JsonElement> ids = arguments.GetProperty("viewIds").EnumerateArray().ToList();
        if (ids.Count == 0)
        {
            throw new ToolException(T["Tool.MoveNeedsElements"]);
        }

        var described = ids
            .Select(id => id.ValueKind == JsonValueKind.Number ? Target(document, id.GetInt64(), template).Name : id.GetString()!)
            .ToList();
        string list = string.Join(", ", described.Take(MaxListed)) + (described.Count > MaxListed ? ", …" : "");
        return T.Format("Tool.TemplateSummary", template.Name, ids.Count, list);
    }

    public override OperationResult Apply(Document document, JsonElement arguments)
    {
        View template = Template(document, arguments);
        List<View> views = arguments.GetProperty("viewIds").EnumerateArray().Select(id => Target(document, id.GetInt64(), template)).ToList();
        foreach (View view in views)
        {
            view.ViewTemplateId = template.Id;
        }

        return new OperationResult(views[0].Id.Value, T.Format("Tool.TemplateDone", template.Name, views.Count),
            views.Skip(1).Select(v => v.Id.Value).ToList());
    }

    private View Template(Document document, JsonElement arguments)
    {
        long id = arguments.GetProperty("templateId").GetInt64();
        return document.GetElement(new ElementId(id)) is View { IsTemplate: true } template
            ? template
            : throw new ToolException(T.Format("Tool.NotATemplate", id));
    }

    private View Target(Document document, long id, View template)
    {
        View view = document.GetElement(new ElementId(id)) as View ?? throw new ToolException(T.Format("Tool.NotAView", id));
        return !view.IsTemplate && view.IsValidViewTemplate(template.Id)
            ? view
            : throw new ToolException(T.Format("Tool.TemplateInvalid", template.Name, view.Name, view.ViewType));
    }
}

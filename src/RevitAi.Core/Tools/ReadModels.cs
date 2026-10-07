namespace RevitAi.Core.Tools;

// Result contracts of the read-only tools (docs/REVIT_TOOLS.md).
// Lengths and coordinates in millimetres, areas in m² (ADR-026). IDs are ElementId.Value.

public sealed record ElementSummary(long Id, string? Category, string? Family, string? Type, long? TypeId, string? Name, string? Level);

public sealed record ProjectInfoResult(
    string Title,
    string? ProjectNumber,
    string? ProjectName,
    string RevitVersion,
    string LengthDisplayUnit,
    string AreaDisplayUnit);

public sealed record ViewResult(long Id, string Name, string ViewType, long? LevelId, string? LevelName);

public sealed record LevelResult(long Id, string Name, double ElevationMm);

public sealed record ActiveLevelResult(LevelResult? Level, string? Note);

public sealed record ElementListResult(int TotalCount, bool Truncated, IReadOnlyList<ElementSummary> Elements);

public sealed record PointMm(double X, double Y, double Z);

public sealed record LocationResult(string Kind, PointMm? Point, PointMm? Start, PointMm? End, double? LengthMm);

public sealed record ElementDetails(ElementSummary Element, LocationResult? Location);

public sealed record ElementParameter(
    string Name,
    string Source,
    string StorageType,
    string? DisplayValue,
    double? ValueMm,
    double? ValueM2,
    bool IsReadOnly);

public sealed record FamilyTypeInfo(
    long TypeId,
    string? Family,
    string Type,
    bool IsDefault,
    int? PreferredRank,
    int InstanceCount);

public sealed record FamilyTypesResult(string Category, int TotalCount, bool Truncated, IReadOnlyList<FamilyTypeInfo> Types);

public sealed record StandardTypesResult(
    string Category,
    bool Configured,
    IReadOnlyList<FamilyTypeInfo> Preferred,
    IReadOnlyList<string> NotFoundInProject,
    string? Note);

public sealed record NearbyElement(ElementSummary Element, double CenterDistanceMm);

public sealed record NearbyElementsResult(long ElementId, double RadiusMm, int TotalCount, bool Truncated, IReadOnlyList<NearbyElement> Elements);

public sealed record RoomInfo(long Id, string? Number, string? Name, string? Level, double AreaM2);

public sealed record ElementRoomResult(long ElementId, RoomInfo? Room, RoomInfo? FromRoom, RoomInfo? ToRoom, string? Note);

public sealed record BoundarySegmentInfo(long? ElementId, string? Category, PointMm Start, PointMm End, double LengthMm);

public sealed record RoomBoundaryResult(RoomInfo Room, IReadOnlyList<IReadOnlyList<BoundarySegmentInfo>> Loops, string? Note);

public sealed record ViewInfo(long Id, string Name, string ViewType, string? Level, string? SheetNumber, bool IsTemplate);

public sealed record ViewListResult(int TotalCount, bool Truncated, IReadOnlyList<ViewInfo> Views);

public sealed record QaElement(ElementSummary Element, string? Reason);

public sealed record QaResult(string Check, int TotalCount, bool Truncated, IReadOnlyList<QaElement> Elements, string? Note);

public sealed record DuplicatesResult(int GroupCount, bool Truncated, IReadOnlyList<IReadOnlyList<ElementSummary>> Groups);

public sealed record WarningGroup(string Description, int Count, IReadOnlyList<long> SampleElementIds);

public sealed record WarningsResult(int TotalWarnings, int GroupCount, bool Truncated, IReadOnlyList<WarningGroup> Groups);

public sealed record SelectionChangeResult(int Selected, IReadOnlyList<long> NotFound);

public sealed record RuleResult(string Rule, string Description, int ViolationCount, bool Truncated, IReadOnlyList<QaElement> Violations);

public sealed record StandardsReport(bool Configured, IReadOnlyList<RuleResult> Rules, IReadOnlyList<string> Problems);

public sealed record StandardsSummary(
    IReadOnlyList<string>? RoomNames,
    string? SheetNumberPattern,
    IReadOnlyDictionary<string, string>? ViewNamePatterns,
    IReadOnlyDictionary<string, List<string>>? RequiredParameters,
    IReadOnlyDictionary<string, string>? ViewTemplates,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StandardTypes,
    IReadOnlyList<string> Problems);

public sealed record ParametersResult(
    long ElementId,
    IReadOnlyList<ElementParameter> Parameters,
    IReadOnlyList<string> NotFound,
    bool Truncated);

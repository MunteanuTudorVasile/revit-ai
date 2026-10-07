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

public sealed record ParametersResult(
    long ElementId,
    IReadOnlyList<ElementParameter> Parameters,
    IReadOnlyList<string> NotFound,
    bool Truncated);

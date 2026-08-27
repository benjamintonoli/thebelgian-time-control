namespace TheBelgian.TimeControl.Core.Models;

public enum PerformanceActivityType
{
    CustomerWork,
    SiteWork,
    OfficeWork,
    Travel,
    WaitingTime,
    Administration,
    RemoteWork,
    Break,
    Absence,
    OtherNonLocationBound,
    Unknown,
}

public sealed record PerformanceActivityClassification(
    long PerformanceId,
    DateOnly Date,
    string TechnicianName,
    PerformanceActivityType ActivityType,
    bool RequiresGeographicMatch,
    string? MainTaskExternalId,
    string? Description,
    string? ProjectNumber,
    string? ProjectName,
    string? WorkOrderNumber,
    string? DeliveryAddressExternalId,
    string Reason,
    PilotLocationResolutionStatus? LocationMatchStatus,
    bool WasIncludedInLocationDenominator,
    bool IncorrectlyInLocationDenominator);

public sealed class ActivityClassificationAnalysisResult
{
    public required IReadOnlyList<PerformanceActivityClassification> Classifications { get; init; }
    public required IReadOnlyList<ActivityTypeSummary> TypeSummaries { get; init; }
    public required OpenLocationCaseSummary OpenCases { get; init; }
    public required CorrectedMatchSummary CorrectedMatch { get; init; }
    public required string AliasAdvice { get; init; }
}

public sealed record ActivityTypeSummary(
    PerformanceActivityType ActivityType,
    int PerformanceCount,
    IReadOnlyList<string> MainTaskCodes,
    IReadOnlyList<string> Descriptions,
    int RequiresGeographicMatchCount,
    int IncorrectlyInLocationDenominatorCount,
    int UnknownCount);

public sealed record OpenLocationCaseSummary(
    int OpenCaseCount,
    int NotLocationBoundCount,
    int StillLocationBoundCount,
    int UnknownCount,
    IReadOnlyList<PerformanceActivityClassification> Cases);

public sealed record CorrectedMatchSummary(
    int LocationBoundResolutionCount,
    int ReliableLocationBoundCount,
    double CorrectedReliablePercent,
    int RemainingNoReliableMatchCount,
    int AliasFlippableLocationBoundCount,
    double PotentialReliablePercentAfterAliases);

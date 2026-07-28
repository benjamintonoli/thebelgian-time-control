namespace TheBelgian.TimeControl.Core.Models;

public sealed class CoverageGapAnalysisResult
{
    public required string LinkingModelDescription { get; init; }
    public required IReadOnlyList<CoverageGapEmployeeLink> EmployeeLinks { get; init; }
    public required CoverageGapMatchBreakdown MatchBreakdown { get; init; }
    public required IReadOnlyList<CoverageGapLocationGroup> UnreliableGroups { get; init; }
    public required IReadOnlyList<CoverageGapLocationGroup> TopConfirmations { get; init; }
    public required CoverageGapAliasProjection AliasProjection { get; init; }
    public required string AliasTableAdvice { get; init; }
}

public sealed record CoverageGapEmployeeLink(
    string Query,
    string PlenionIdResource,
    string PlenionResCode,
    string PlenionOmschr,
    string PowerfleetDriverId,
    string? PowerfleetDriverName,
    string LinkingKey,
    IReadOnlyList<string> InformativeObjectNames,
    IReadOnlyList<string> InformativePlates);

public sealed record CoverageGapMatchBreakdown(
    int TotalLocationResolutions,
    int ConfirmedCount,
    int ProbableCount,
    int ManualReviewCount,
    int NoReliableMatchCount,
    int AddressDataIssueCount,
    int ReliableCount,
    double ReliablePercent,
    int UnreliableCount,
    double UnreliablePercent,
    string PrimaryCause);

public sealed record CoverageGapLocationGroup(
    string PlenionLocationKey,
    string? PlenionLacleunik,
    string PlenionAddress,
    string? PowerfleetStopAddress,
    double? PowerfleetLatitude,
    double? PowerfleetLongitude,
    int PerformanceCount,
    IReadOnlyList<string> Technicians,
    double? AverageDistanceMeters,
    double AverageTimeOverlapMinutes,
    PilotLocationResolutionStatus DominantMatchStatus,
    string UncertaintyReason,
    bool AliasWouldMakeReliable);

public sealed record CoverageGapAliasProjection(
    int UnreliableWithCandidateStop,
    int UnreliableWithoutCandidateStop,
    int PerformancesFlippedIfAllAliasesConfirmed,
    int UniqueProblemLocations,
    int UniqueConfirmableAliases,
    double PotentialReliablePercentAfterAliasConfirmation,
    int Top20GainPerformances);

using TheBelgian.TimeControl.Core.Configuration;

namespace TheBelgian.TimeControl.Core.Models;

public enum GeocodeQualityClass
{
    PreciseBuilding,
    PreciseAmenity,
    PartialAddress,
    StreetOnly,
    LowConfidence,
    Unusable,
}

public enum AdaptiveMatchDecision
{
    Confirmed,
    Probable,
    Ambiguous,
    Unresolved,
}

public enum AdaptiveDistanceZone
{
    Strong0To100,
    Probable101To250,
    Learned251To500,
    Beyond500,
    Unknown,
}

public sealed record AdaptiveMatchCandidate(
    MergedPilotStop Stop,
    double? DistanceMeters,
    AdaptiveDistanceZone DistanceZone,
    int OverlapMinutes,
    double OverlapPercent,
    int ArrivalDifferenceMinutes,
    int DepartureDifferenceMinutes,
    int StopDurationMinutes,
    bool HasCompetingPerformanceOverlap,
    double GeocodeScore,
    double DistanceScore,
    double TimeScore,
    double OverlapPercentScore,
    double AlignmentScore,
    double HistoricalScore,
    double CompetitionPenalty,
    double TotalScore,
    string? HistoricalClusterId,
    string Explanation);

public sealed record AdaptiveMatchResult(
    long PerformanceId,
    DateOnly Date,
    string TechnicianName,
    string? DeliveryAddressExternalId,
    string PlenionAddress,
    GeocodeQualityClass GeocodeQuality,
    bool UsedAsPrecisePoint,
    AdaptiveMatchDecision Decision,
    AdaptiveMatchCandidate? Selected,
    IReadOnlyList<AdaptiveMatchCandidate> Candidates,
    bool UsedHistoricalCluster,
    AdaptiveDistanceZone DistanceZone,
    string Assessment,
    bool UsedRecovery = false,
    string? RecoveryReason = null);

public sealed record MergedPilotStop(
    string MergedStopId,
    DateOnly Date,
    DateTimeOffset Arrival,
    DateTimeOffset Departure,
    int DurationMinutes,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    string? DriverId,
    string? DriverName,
    IReadOnlyList<string> SourceStopIds,
    bool IsPassThrough);

public sealed record HistoricalLocationCluster(
    string ClusterId,
    string PlenionLocationKey,
    string? DeliveryAddressExternalId,
    double CenterLatitude,
    double CenterLongitude,
    double RadiusMeters,
    int VisitCount,
    int DistinctWorkdayCount,
    int TechnicianCount,
    double AverageDistanceMeters,
    double MedianDistanceMeters,
    double AverageOverlapMinutes,
    double DominancePercentage,
    int CompetingClusterCount,
    DateOnly FirstObserved,
    DateOnly LastObserved,
    double Confidence,
    string CalculationVersion);

public sealed class AdaptiveLocationValidationResult
{
    public required AdaptiveMatcherVariantResult Baseline { get; init; }
    public required AdaptiveMatcherVariantResult AdaptiveWithoutLearning { get; init; }
    public required AdaptiveMatcherVariantResult AdaptiveWithLearning { get; init; }
    public required IReadOnlyList<AdaptiveParameterExperiment> Experiments { get; init; }
    public required AdaptiveParameterExperiment SelectedConfiguration { get; init; }
    public required int LearnedClusterCount { get; init; }
    public required string PrecisionKind { get; init; }
    public required double? PrecisionPercent { get; init; }
    public required string LargestGainRules { get; init; }
    public required bool TargetEightyPercentResponsible { get; init; }
    public required string RecommendedNextStep { get; init; }
    public required string StratifiedSamplePath { get; init; }
}

public sealed record AdaptiveMatcherVariantResult(
    string Name,
    int Confirmed,
    int Probable,
    int Ambiguous,
    int Unresolved,
    int Total,
    double ReliableCoveragePercent,
    IReadOnlyDictionary<string, int> LinksByDistanceZone,
    int LinksViaHistoricalClusters,
    int CompetingCandidateCases,
    int EstimatedFalsePositiveRisk);

public sealed record AdaptiveParameterExperiment(
    string Name,
    AdaptiveLocationMatchingOptions Options,
    AdaptiveMatcherVariantResult WithLearning,
    double EstimatedPrecisionPercent,
    bool MeetsCoverageTarget,
    bool PreferredForPrecision);

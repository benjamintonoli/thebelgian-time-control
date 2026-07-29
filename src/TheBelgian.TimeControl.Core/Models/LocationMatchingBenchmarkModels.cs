namespace TheBelgian.TimeControl.Core.Models;

public sealed class LocationMatchingBenchmarkResult
{
    public required int DeterministicDenominator { get; init; }
    public required IReadOnlyList<long> StablePerformanceIds { get; init; }
    public required PowerfleetGranularitySummary PowerfleetGranularity { get; init; }
    public required int BenchmarkCaseCount { get; init; }
    public required HistoricalClusterBenchmarkStatus HistoricalClustering { get; init; }
    public required IReadOnlyList<string> VariantsReady { get; init; }
    public required string NeedsForMeasuredMetrics { get; init; }
    public required string BenchmarkPath { get; init; }
    public required IReadOnlyList<string> CompleteMonths { get; init; }
    public required int DevelopmentCaseCount { get; init; }
    public required int HoldoutCaseCount { get; init; }
    public required int HoldoutUniqueLocationCount { get; init; }
    public required int ChallengeCaseCount { get; init; }
    public required int SeenLocationCount { get; init; }
    public required int UnseenLocationCount { get; init; }
    public required string DevelopmentPath { get; init; }
    public required string HoldoutPath { get; init; }
    public required string ChallengePath { get; init; }
    public required string CompletenessPath { get; init; }
    public required string BlindReviewerPath { get; init; }
}

public sealed class PowerfleetGranularitySummary
{
    public required IReadOnlyList<string> ReportParameters { get; init; }
    public required IReadOnlyList<string> AvailableFields { get; init; }
    public required bool HasVendorStops { get; init; }
    public required bool HasTripStartEndCoordinates { get; init; }
    public required bool HasIndividualPoints { get; init; }
    public required bool HasTimestamps { get; init; }
    public required bool HasSpeed { get; init; }
    public required bool HasIgnition { get; init; }
    public required bool HasGpsValidityOrAccuracy { get; init; }
    public required string Limitation { get; init; }
}

public sealed class HistoricalClusterBenchmarkStatus
{
    public required DateOnly HistoryFrom { get; init; }
    public required DateOnly HistoryThrough { get; init; }
    public required bool JulyUsedForLearning { get; init; }
    public required int LearnedClusterCount { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record LocationMatchingBenchmarkCase
{
    public required long PerformanceId { get; init; }
    public required string Technician { get; init; }
    public required DateOnly Date { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? Lacleunik { get; init; }
    public required string PlenionAddress { get; init; }
    public required GeocodeQualityClass GeocodeQuality { get; init; }
    public required string ExistingMatchStatus { get; init; }
    public string? ActivityType { get; init; }
    public string? LocationExposure { get; init; }
    public string? DatasetRole { get; init; }
    public string? PreviousPerformance { get; init; }
    public string? NextPerformance { get; init; }
    public required IReadOnlyList<LocationMatchingBenchmarkCandidate> Candidates { get; init; }
    public string? Label { get; init; }
    public string? ExpectedStopId { get; init; }
    public string? ReviewerConfidence { get; init; }
    public string? ReviewerNote { get; init; }
    public string? SecondReviewLabel { get; init; }
    public string? SecondReviewExpectedStopId { get; init; }
    public string? SecondReviewerConfidence { get; init; }
    public string? SecondReviewerNote { get; init; }
    public bool RequiresSecondReview { get; init; }
    public string? AdjudicationStatus { get; init; }
    public bool IsChallengeSubset { get; init; }
    public bool IsCalibrationCase { get; init; }
}

public sealed record LocationMatchingBenchmarkCandidate
{
    public required string StopId { get; init; }
    public string? Address { get; init; }
    public double? DistanceMeters { get; init; }
    public required DateTimeOffset Arrival { get; init; }
    public required DateTimeOffset Departure { get; init; }
    public required int OverlapMinutes { get; init; }
    public required int StartDifferenceMinutes { get; init; }
    public required int EndDifferenceMinutes { get; init; }
    public required string ExistingCandidateStatus { get; init; }
    public required int ExistingCandidateScore { get; init; }
    public required string Explanation { get; init; }
}

public sealed class LocationMatchingHoldoutFile
{
    public required bool Locked { get; init; }
    public required bool DoNotUseForOptimization { get; init; }
    public required string Warning { get; init; }
    public required IReadOnlyList<LocationMatchingBenchmarkCase> Cases { get; init; }
}

public sealed class LocationMatchingDevelopmentFile
{
    public required string DatasetRole { get; init; }
    public required IReadOnlyList<LocationMatchingBenchmarkCase> Cases { get; init; }
    public required BenchmarkEvaluationScaffold Evaluation { get; init; }
}

public sealed class LocationMatchingChallengeFile
{
    public required string DatasetRole { get; init; }
    public required string ExclusionNote { get; init; }
    public required IReadOnlyList<LocationMatchingBenchmarkCase> Cases { get; init; }
    public required BenchmarkEvaluationScaffold Evaluation { get; init; }
}

public sealed class MonthTechnicianCompleteness
{
    public required string Technician { get; init; }
    public required string YearMonth { get; init; }
    public required int LocationBoundPerformances { get; init; }
    public required int PowerfleetTrips { get; init; }
    public required int PerformancesWithCandidateStops { get; init; }
    public required int UniqueLacleunikCount { get; init; }
    public required int MissingDriverTripCount { get; init; }
    public required bool IsComplete { get; init; }
    public required string Notes { get; init; }
}

public sealed class HoldoutSamplingManifest
{
    public required int RandomSeed { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required bool Locked { get; init; }
    public required int TargetCaseCount { get; init; }
    public required int MaxCasesPerLacleunik { get; init; }
    public required int MinUniqueLacleunik { get; init; }
    public required IReadOnlyList<long> SelectedPerformanceIds { get; init; }
    public required IReadOnlyList<string> CompleteMonthsUsed { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByTechnician { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByMonth { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByExposure { get; init; }
    public string? HoldoutPeriodFrom { get; init; }
    public string? HoldoutPeriodThrough { get; init; }
    public string? HistoricalFeaturesThroughExclusive { get; init; }
    public string? ContentSha256 { get; init; }
    public string? IndependenceNote { get; init; }
}

public sealed class LocationMatchingCalibrationFile
{
    public required string DatasetRole { get; init; }
    public required int RandomSeed { get; init; }
    public required IReadOnlyList<LocationMatchingBenchmarkCase> Cases { get; init; }
    public BenchmarkLabelAgreement? Agreement { get; init; }
}

public sealed class BenchmarkLabelAgreement
{
    public required int CaseCount { get; init; }
    public required int DoubleLabeledCount { get; init; }
    public required int ExactLabelAgreementCount { get; init; }
    public required double ExactLabelAgreementRate { get; init; }
    public required int ExpectedStopIdAgreementCount { get; init; }
    public required double ExpectedStopIdAgreementRate { get; init; }
    public required int ConflictCount { get; init; }
    public required double CohensKappa { get; init; }
    public required string Status { get; init; }
}

public sealed class BenchmarkLeakageAudit
{
    public required IReadOnlyList<BenchmarkDatasetAuditRow> Datasets { get; init; }
    public required int PerformanceIdOverlapDevHoldout { get; init; }
    public required int PerformanceIdOverlapDevChallenge { get; init; }
    public required int PerformanceIdOverlapHoldoutChallenge { get; init; }
    public required int TechDatePerformanceOverlapDevHoldout { get; init; }
    public required int HoldoutInHistoricalLearningWindowCount { get; init; }
    public required int HoldoutInMayJun2026Count { get; init; }
    public required int HoldoutInJuly2026Count { get; init; }
    public required bool MayOrJuneUsedAsBothHistoricalAndHoldout { get; init; }
    public required IReadOnlyList<string> Findings { get; init; }
}

public sealed class BenchmarkDatasetAuditRow
{
    public required string Name { get; init; }
    public required string PeriodFrom { get; init; }
    public required string PeriodThrough { get; init; }
    public required int CaseCount { get; init; }
    public required int UniquePerformanceIds { get; init; }
    public required int UniqueLacleuniks { get; init; }
}

public sealed class LocationMatchingPurifyResult
{
    public required BenchmarkLeakageAudit PriorLeakage { get; init; }
    public required string DevelopmentRole { get; init; }
    public required string ChallengeRole { get; init; }
    public required string HoldoutPeriod { get; init; }
    public required int PureHoldoutCaseCount { get; init; }
    public required int HoldoutUniqueLocationCount { get; init; }
    public required int CalibrationCaseCount { get; init; }
    public required string CalibrationReviewerPath { get; init; }
    public required string HoldoutContentSha256 { get; init; }
    public required int DevelopmentCaseCount { get; init; }
    public required int ChallengeCaseCount { get; init; }
}

public sealed class BenchmarkEvaluationScaffold
{
    public required bool LabelsPresent { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<string> PreparedMetrics { get; init; }
}

public sealed class BenchmarkLabeledMetrics
{
    public required int CaseCount { get; init; }
    public required int TruePositives { get; init; }
    public required int FalsePositives { get; init; }
    public required int FalseNegatives { get; init; }
    public required int TrueNegatives { get; init; }
    public required double Precision { get; init; }
    public required double Recall { get; init; }
    public required double Coverage { get; init; }
    public required double F1 { get; init; }
    public required WilsonInterval Wilson95 { get; init; }
    public BenchmarkLabeledMetricsSlice? SeenLocation { get; init; }
    public BenchmarkLabeledMetricsSlice? UnseenLocation { get; init; }
}

public sealed class BenchmarkLabeledMetricsSlice
{
    public required int CaseCount { get; init; }
    public required double Precision { get; init; }
    public required double Recall { get; init; }
    public required double Coverage { get; init; }
    public required double F1 { get; init; }
}

public sealed class WilsonInterval
{
    public required double Lower { get; init; }
    public required double Upper { get; init; }
}

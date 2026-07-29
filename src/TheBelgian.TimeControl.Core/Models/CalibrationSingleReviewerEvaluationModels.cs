namespace TheBelgian.TimeControl.Core.Models;

public sealed class CalibrationSingleReviewerEvaluationResult
{
    public required string ReferenceSet { get; init; }
    public required int CaseCount { get; init; }
    public required int HighConfidenceCaseCount { get; init; }
    public required IReadOnlyList<CalibrationVariantMetrics> Variants { get; init; }
    public required string BestVariant { get; init; }
    public required IReadOnlyList<string> MainErrorCauses { get; init; }
    public required string RecommendedNextStep { get; init; }
    public required int LearnedClusterCount { get; init; }
    public required IReadOnlyList<CalibrationGapCaseAnalysis> GapAnalysis { get; init; }
    public required IReadOnlyList<long> RecoveredPerformanceIds { get; init; }
    public required bool HybridAcceptanceCriteriaMet { get; init; }
    public required string HybridAcceptanceNotes { get; init; }
    public required DevelopmentHybridSanityCheck DevelopmentSanityCheck { get; init; }
}

public sealed class CalibrationVariantMetrics
{
    public required string Name { get; init; }
    public required CalibrationMetricSlice AllCases { get; init; }
    public required CalibrationMetricSlice HighConfidenceOnly { get; init; }
    public required IReadOnlyList<CalibrationCaseError> Errors { get; init; }
}

public sealed class CalibrationMetricSlice
{
    public required int CaseCount { get; init; }
    public required int AcceptedMatches { get; init; }
    public required int CorrectAcceptedMatches { get; init; }
    public required double Precision { get; init; }
    public required double Coverage { get; init; }
    public required int FalsePositives { get; init; }
    public required int FalseNegatives { get; init; }
    public required int WrongStopIdChoices { get; init; }
}

public sealed class CalibrationCaseError
{
    public required long PerformanceId { get; init; }
    public required string Label { get; init; }
    public required string? ExpectedStopId { get; init; }
    public required string ReviewerConfidence { get; init; }
    public required string Reason { get; init; }
    public required string PredictedDecision { get; init; }
    public string? PredictedStopId { get; init; }
}

public sealed class CalibrationGapCaseAnalysis
{
    public required long PerformanceId { get; init; }
    public required string Label { get; init; }
    public required bool BaselineAccepted { get; init; }
    public required bool AdaptiveUnresolved { get; init; }
    public required bool IsRecoverableGap { get; init; }
    public required double? DistanceMeters { get; init; }
    public required int OverlapMinutes { get; init; }
    public required double OverlapPercent { get; init; }
    public required int ArrivalVersusStartMinutes { get; init; }
    public required int DepartureVersusEndMinutes { get; init; }
    public required string GeocodeQuality { get; init; }
    public required int CompetingCandidateCount { get; init; }
    public required double? ScoreMarginVsSecond { get; init; }
    public required string? PreviousPerformance { get; init; }
    public required string? NextPerformance { get; init; }
    public required string AdaptiveAbstentionReason { get; init; }
    public required bool HybridRecovered { get; init; }
    public string? HybridRecoveryReason { get; init; }
}

public sealed class DevelopmentHybridSanityCheck
{
    public required int CaseCount { get; init; }
    public required int Accepted { get; init; }
    public required int Unresolved { get; init; }
    public required int Ambiguous { get; init; }
    public required int RecoveryOnlyMatches { get; init; }
    public required IReadOnlyDictionary<string, int> RecoveryByDistanceZone { get; init; }
    public required IReadOnlyDictionary<string, int> RecoveryByOverlapZone { get; init; }
    public required IReadOnlyList<string> NotableRiskPatterns { get; init; }
}

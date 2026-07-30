namespace TheBelgian.TimeControl.Core.Models;

public sealed class LockedHoldoutStartedMarker
{
    public required DateTimeOffset StartedAt { get; init; }
    public required string GitCommit { get; init; }
    public required string Note { get; init; }
}

public sealed class LockedHoldoutFinalReport
{
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required string GitCommit { get; init; }
    public string? GitTag { get; init; }
    public required string ConfigurationHashSha256 { get; init; }
    public required string HoldoutManifestHashSha256 { get; init; }
    public required string HoldoutContentSha256 { get; init; }
    public required int CaseCount { get; init; }
    public required IReadOnlyDictionary<string, int> LabelDistribution { get; init; }
    public required int AcceptedMatches { get; init; }
    public required int CorrectAcceptedMatches { get; init; }
    public required double Precision { get; init; }
    public required double Coverage { get; init; }
    public required int FalsePositives { get; init; }
    public required int FalseNegatives { get; init; }
    public required int WrongVisitCandidateChoices { get; init; }
    public required int Abstentions { get; init; }
    public FrozenMatcherMetricSlice? HighConfidence { get; init; }
    public required IReadOnlyDictionary<string, FrozenMatcherMetricSlice> ByDistanceZone { get; init; }
    public required IReadOnlyDictionary<string, FrozenMatcherMetricSlice> ByOverlapZone { get; init; }
    public required IReadOnlyDictionary<string, FrozenMatcherMetricSlice> ByGeocodeQuality { get; init; }
    public required IReadOnlyList<LockedHoldoutErrorRow> Errors { get; init; }
    public required IReadOnlyDictionary<string, int> ErrorCategories { get; init; }
    public required bool SystematicFalsePositivePattern { get; init; }
    public required string Decision { get; init; }
    public required IReadOnlyList<string> DecisionNotes { get; init; }
    public required bool ExternalDataAccessed { get; init; }
    public required bool HoldoutOpened { get; init; }
}

public sealed class LockedHoldoutErrorRow
{
    public required long PerformanceId { get; init; }
    public required string Label { get; init; }
    public required string Category { get; init; }
    public required string PredictedDecision { get; init; }
    public string? PredictedStopId { get; init; }
    public string? ReviewerConfidence { get; init; }
    public string? DistanceZone { get; init; }
    public string? OverlapZone { get; init; }
    public string? GeocodeQuality { get; init; }
    public required string Diagnostics { get; init; }
}

public sealed class LockedHoldoutEvaluationResult
{
    public required bool Completed { get; init; }
    public required int ExitCode { get; init; }
    public required string Decision { get; init; }
    public required string FinalJsonPath { get; init; }
    public required string FinalMarkdownPath { get; init; }
    public required string StartedMarkerPath { get; init; }
    public LockedHoldoutFinalReport? Report { get; init; }
    public required IReadOnlyList<string> Messages { get; init; }
}

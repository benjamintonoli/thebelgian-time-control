namespace TheBelgian.TimeControl.Core.Models;

public sealed class RecoveryAuditSetFile
{
    public required string DatasetRole { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public required int RandomSeed { get; init; }
    public required int CaseCount { get; init; }
    public required RecoveryAuditDistribution Distribution { get; init; }
    public required string BlindNote { get; init; }
    public required IReadOnlyList<RecoveryAuditCase> Cases { get; init; }
}

public sealed class RecoveryAuditDistribution
{
    public required int RecoveryOnly { get; init; }
    public required int AdaptiveAcceptedControl { get; init; }
    public required int AbstentionControl { get; init; }
    public required int WeakOverlapRecovery { get; init; }
    public required int ProbableDistanceRecovery { get; init; }
    public required int WeakGeocodeRecovery { get; init; }
    public required int Total { get; init; }
}

public sealed record RecoveryAuditCase
{
    public required long PerformanceId { get; init; }
    public required string Technician { get; init; }
    public required DateOnly Date { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? Lacleunik { get; init; }
    public required string PlenionAddress { get; init; }
    public string? PreviousPerformance { get; init; }
    public string? NextPerformance { get; init; }
    public required IReadOnlyList<LocationMatchingBenchmarkCandidate> Candidates { get; init; }
    public required IReadOnlyList<string> Strata { get; init; }

    /// <summary>Internal only; never shown in blind pack.</summary>
    public required string AdaptiveDecision { get; init; }

    /// <summary>Internal only; never shown in blind pack.</summary>
    public required string HybridDecision { get; init; }

    public required bool UsedRecovery { get; init; }
    public string? SelectedStopId { get; init; }
    public IReadOnlyList<string>? SelectedSourceStopIds { get; init; }
    public double? SelectedDistanceMeters { get; init; }
    public int? SelectedOverlapMinutes { get; init; }
    public double? SelectedOverlapPercent { get; init; }
    public string? DistanceZone { get; init; }
    public string? GeocodeQuality { get; init; }
    public string? Label { get; init; }
    public string? ExpectedStopId { get; init; }
    public IReadOnlyList<string>? ExpectedVisitStopIds { get; init; }
    public string? ReviewerConfidence { get; init; }
    public string? ReviewerNote { get; init; }
}

public sealed class RecoveryAuditExportResult
{
    public required string MarkdownPath { get; init; }
    public required string LabelsPath { get; init; }
    public required string SetPath { get; init; }
    public required int CaseCount { get; init; }
    public required int NewRecoveryOnlyCount { get; init; }
    public required RecoveryAuditDistribution Distribution { get; init; }
}

public sealed class RecoveryAuditLabelImportResult
{
    public required int ImportedCount { get; init; }
    public required int LabeledCount { get; init; }
    public required string LabelsPath { get; init; }
    public required string SetPath { get; init; }
}

public sealed class RecoveryAuditEvaluationResult
{
    public required int CaseCount { get; init; }
    public required int LabeledCount { get; init; }
    public required bool LabelsComplete { get; init; }
    public required string Status { get; init; }
    public RecoveryAuditMetricSlice? RecoveryOnly { get; init; }
    public RecoveryAuditMetricSlice? WeakOverlapRecovery { get; init; }
    public IReadOnlyDictionary<string, RecoveryAuditMetricSlice>? ByDistanceZone { get; init; }
    public IReadOnlyDictionary<string, RecoveryAuditMetricSlice>? ByGeocodeQuality { get; init; }
    public RecoveryAuditMetricSlice? AllLabeledHybrid { get; init; }
    public IReadOnlyList<RecoveryAuditCaseError> Errors { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class RecoveryAuditMetricSlice
{
    public required int CaseCount { get; init; }
    public required int AcceptedMatches { get; init; }
    public required int CorrectAcceptedMatches { get; init; }
    public required double Precision { get; init; }
    public required int FalsePositives { get; init; }
    public required int FalseNegatives { get; init; }
    public required int WrongStopIdChoices { get; init; }
}

public sealed class RecoveryAuditCaseError
{
    public required long PerformanceId { get; init; }
    public required string Stratum { get; init; }
    public required string Label { get; init; }
    public required string HybridDecision { get; init; }
    public required string Reason { get; init; }
    public string? PredictedStopId { get; init; }
    public string? ExpectedStopId { get; init; }
}

/// <summary>
/// Classified development case used only for recovery-audit sampling (not blind-exposed).
/// </summary>
public sealed record RecoveryAuditClassifiedCase(
    long PerformanceId,
    LocationMatchingBenchmarkCase Source,
    bool UsedRecovery,
    bool AdaptiveAccepted,
    bool HybridAbstention,
    string AdaptiveDecision,
    string HybridDecision,
    string? SelectedStopId,
    IReadOnlyList<string> SelectedSourceStopIds,
    double? SelectedDistanceMeters,
    int? SelectedOverlapMinutes,
    double? SelectedOverlapPercent,
    string? DistanceZone,
    string? GeocodeQuality,
    IReadOnlyList<string> Strata);

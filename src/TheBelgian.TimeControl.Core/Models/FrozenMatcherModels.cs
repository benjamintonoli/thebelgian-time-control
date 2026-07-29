namespace TheBelgian.TimeControl.Core.Models;

public sealed class FrozenMatcherManifest
{
    public required string MatcherVersion { get; init; }
    public required string GitCommit { get; init; }
    public required string ConfigurationHashSha256 { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required AdaptiveLocationMatchingOptionsSnapshot Options { get; init; }
    public required FrozenMatcherInputHashes InputFileSha256 { get; init; }
    public required string HoldoutPolicy { get; init; }
    public required string Mode { get; init; }
}

public sealed class AdaptiveLocationMatchingOptionsSnapshot
{
    public required string CalculationVersion { get; init; }
    public required double StrongDistanceMeters { get; init; }
    public required double ProbableDistanceMeters { get; init; }
    public required double MaximumLearnedClusterDistanceMeters { get; init; }
    public required double MinimumOverlapMinutes { get; init; }
    public required double MinimumOverlapPercent { get; init; }
    public required double StrongOverlapPercent { get; init; }
    public required double VisitMergeDistanceMeters { get; init; }
    public required double VisitMergeMaxGapMinutes { get; init; }
    public required bool EnablePrecisionPreservingRecovery { get; init; }
    public required double RecoveryMaximumDistanceMeters { get; init; }
    public required double RecoveryMinimumOverlapMinutes { get; init; }
    public required double RecoveryMinimumOverlapPercent { get; init; }
    public required double RecoveryStrongOverlapMinutes { get; init; }
    public required double RecoveryStrongOverlapPercent { get; init; }
    public required double RecoveryMinimumScoreMargin { get; init; }
    public required double RecoveryShortChainMinOverlapMinutes { get; init; }
    public required double RecoveryShortChainMinCombinedOverlapPercent { get; init; }
}

public sealed class FrozenMatcherInputHashes
{
    public required string LocationMatchingCalibrationJson { get; init; }
    public required string CalibrationLabelsReviewer1Json { get; init; }
    public required string RecoveryAuditSetJson { get; init; }
    public required string RecoveryAuditLabelsReviewer1Json { get; init; }
}

public sealed class FrozenMatcherVerificationResult
{
    public required bool Passed { get; init; }
    public required int ExitCode { get; init; }
    public required string GitCommit { get; init; }
    public required string ConfigurationHashSha256 { get; init; }
    public required string ManifestPath { get; init; }
    public required string ReportPath { get; init; }
    public required bool ExternalDataAccessed { get; init; }
    public required bool HoldoutOpened { get; init; }
    public required FrozenMatcherMetricSlice Calibration { get; init; }
    public required FrozenMatcherMetricSlice RecoveryOnly { get; init; }
    public required FrozenMatcherMetricSlice AllLabeledHybrid { get; init; }
    public required IReadOnlyList<FrozenMatcherRegressionCheck> RegressionChecks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed record FrozenMatcherMetricSlice
{
    public required int CaseCount { get; init; }
    public required int AcceptedMatches { get; init; }
    public required int CorrectAcceptedMatches { get; init; }
    public required double Precision { get; init; }
    public double? Coverage { get; init; }
    public required int FalsePositives { get; init; }
    public required int FalseNegatives { get; init; }
    public required int WrongVisitCandidateChoices { get; init; }
}

public sealed class FrozenMatcherRegressionCheck
{
    public required long PerformanceId { get; init; }
    public required string Expectation { get; init; }
    public required bool Passed { get; init; }
    public required string Observed { get; init; }
}

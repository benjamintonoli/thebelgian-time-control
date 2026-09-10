using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Actions;

// Re-export trip evidence for eligibility callers without finding-layer coupling noise.

public sealed record PayrollActionEligibilityContext(
    bool IsEmployeeIncluded,
    bool IsMonthFinalized,
    bool HasMatchingExistingPerformance,
    int? ProvenMainTaskId,
    DateTimeOffset? ExistingPerformanceStart,
    DateTimeOffset? ExistingPerformanceEnd,
    long? ExistingPerformanceId,
    string? ExistingActivityType = null,
    long? ExistingMainTaskExternalId = null,
    /// <summary>
    /// When set, overrides automatic GPS-travel classification. Only use when payable work is proven.
    /// </summary>
    PayrollIntervalSemantics? IntervalSemanticsOverride = null,
    /// <summary>Day-level GPS trips for complete-callout assessment (standby adjust).</summary>
    IReadOnlyList<StandbyGpsTripEvidence>? StandbyDayTrips = null,
    /// <summary>Optional precomputed callout assessment (tests / overrides).</summary>
    StandbyCalloutAssessment? CalloutAssessmentOverride = null,
    /// <summary>True when a StandbyPossibleWrongDossier (or similar) finding exists for the same performance.</summary>
    bool HasRelatedDossierAmbiguity = false,
    /// <summary>True when another non-standby performance overlaps the proposed interval.</summary>
    bool HasConflictingPerformance = false);

public sealed record PayrollActionEligibilityResult(
    PayrollProposedActionType ActionType,
    PayrollProposedActionStatus Status,
    PayrollIntervalSemantics IntervalSemantics,
    PayrollActionBlockReasonCode BlockReasonCode,
    string? BlockReason,
    PayrollActionCreateProposal? CreateProposal,
    PayrollActionAdjustProposal? AdjustProposal,
    PayrollActionEvidenceSnapshot EvidenceSnapshot,
    string SourceRevision,
    PayrollActionDeleteProposal? DeleteProposal = null);

public sealed record PayrollActionCreateProposal(
    string ResourceId,
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal Hours,
    string ProjectId,
    string? BonNr,
    int MainTaskId,
    PayrollIntervalSemantics IntervalSemantics,
    TimeSpan Pause = default,
    decimal GrossHours = 0m,
    PayrollPrimaryTimingSource PrimaryTimingSource = PayrollPrimaryTimingSource.Manual,
    string? SupportingEvidenceNl = null,
    string? PauseExplanationNl = null,
    string? OverlapExplanationNl = null);

public sealed record PayrollActionAdjustProposal(
    long PerformanceId,
    DateTimeOffset CurrentStart,
    DateTimeOffset CurrentEnd,
    DateTimeOffset ProposedStart,
    DateTimeOffset ProposedEnd,
    string? ExpectedActivityType,
    long? ExpectedMainTaskExternalId,
    TimeSpan? CurrentPause = null,
    TimeSpan? ProposedPause = null,
    decimal? CurrentAtl = null,
    decimal? ProposedAtl = null,
    string? PauseExplanationNl = null,
    string? OverlapExplanationNl = null);

public sealed record PayrollActionDeleteProposal(
    long PerformanceId,
    DateOnly Date,
    DateTimeOffset CurrentStart,
    DateTimeOffset CurrentEnd,
    decimal AtlHours,
    string ResourceId,
    string ProjectId,
    string? BonNr,
    long? ExpectedMainTaskExternalId,
    string? ExpectedActivityType,
    string? PrestOmschr,
    string? PrestMemo,
    string? BonTechnicianRemark,
    string? ProjectLabel);

public sealed record PayrollActionEvidenceSnapshot(
    string FindingKey,
    PayrollFindingType FindingType,
    PayrollFindingSeverity Severity,
    string? GpsClassification,
    string Evidence,
    string Title,
    string Description,
    IReadOnlyList<long> RelatedPerformanceIds,
    DateTimeOffset? SuggestedPayableStart,
    DateTimeOffset? SuggestedPayableEnd,
    decimal? SuggestedPayableHours,
    string? SuggestedProjectId,
    string? SuggestedBonNr,
    IReadOnlyList<string>? SourceFindingKeys = null,
    IReadOnlyList<int>? SourceFindingIds = null,
    string? CalloutEvidence = null);

public sealed record PayrollActionConfirmationView(
    Guid ActionId,
    int ShadowMonthId,
    int Year,
    int Month,
    string ResourceId,
    string? DisplayName,
    PayrollProposedActionType ActionType,
    PayrollProposedActionStatus Status,
    string? BlockReason,
    PayrollActionEvidenceSnapshot Evidence,
    PayrollActionCreateProposal? CreateProposal,
    PayrollActionAdjustProposal? AdjustProposal,
    string DefaultComment,
    bool ExecutionEnabled,
    bool CanExecute,
    string? ExecutionGateMessage,
    PayrollActionDeleteProposal? DeleteProposal = null);

public sealed record PayrollActionExecutionResult(
    Guid ActionId,
    PayrollProposedActionStatus Status,
    string Message,
    string? PwsReference,
    long? ResultPerformanceId);

public sealed class PayrollActionCreateProposalDto
{
    public string ResourceId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public decimal Hours { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string? BonNr { get; set; }
    public int MainTaskId { get; set; }
    public PayrollIntervalSemantics IntervalSemantics { get; set; }
    public TimeSpan Pause { get; set; }
    public decimal GrossHours { get; set; }
    public PayrollPrimaryTimingSource PrimaryTimingSource { get; set; }
    public string? SupportingEvidenceNl { get; set; }
    public string? PauseExplanationNl { get; set; }
    public string? OverlapExplanationNl { get; set; }
}

public sealed class PayrollActionAdjustProposalDto
{
    public long PerformanceId { get; set; }
    public DateTimeOffset CurrentStart { get; set; }
    public DateTimeOffset CurrentEnd { get; set; }
    public DateTimeOffset ProposedStart { get; set; }
    public DateTimeOffset ProposedEnd { get; set; }
    public string? ExpectedActivityType { get; set; }
    public long? ExpectedMainTaskExternalId { get; set; }
    public TimeSpan? CurrentPause { get; set; }
    public TimeSpan? ProposedPause { get; set; }
    public decimal? CurrentAtl { get; set; }
    public decimal? ProposedAtl { get; set; }
    public string? PauseExplanationNl { get; set; }
    public string? OverlapExplanationNl { get; set; }
}

public sealed class PayrollActionDeleteProposalDto
{
    public long PerformanceId { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset CurrentStart { get; set; }
    public DateTimeOffset CurrentEnd { get; set; }
    public decimal AtlHours { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string? BonNr { get; set; }
    public long? ExpectedMainTaskExternalId { get; set; }
    public string? ExpectedActivityType { get; set; }
    public string? PrestOmschr { get; set; }
    public string? PrestMemo { get; set; }
    public string? BonTechnicianRemark { get; set; }
    public string? ProjectLabel { get; set; }
}

public sealed class PayrollActionEvidenceSnapshotDto
{
    public string FindingKey { get; set; } = string.Empty;
    public PayrollFindingType FindingType { get; set; }
    public PayrollFindingSeverity Severity { get; set; }
    public string? GpsClassification { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long[] RelatedPerformanceIds { get; set; } = [];
    public DateTimeOffset? SuggestedPayableStart { get; set; }
    public DateTimeOffset? SuggestedPayableEnd { get; set; }
    public decimal? SuggestedPayableHours { get; set; }
    public string? SuggestedProjectId { get; set; }
    public string? SuggestedBonNr { get; set; }
    public string[]? SourceFindingKeys { get; set; }
    public int[]? SourceFindingIds { get; set; }
    public string? CalloutEvidence { get; set; }
}

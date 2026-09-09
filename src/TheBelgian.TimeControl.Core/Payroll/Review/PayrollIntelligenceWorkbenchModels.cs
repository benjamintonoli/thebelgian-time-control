using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollIntelligenceWorkbenchPage(
    int Year,
    int Month,
    PayrollReviewCategory Category,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> Cases,
    string? SelectedKey,
    PayrollIntelligenceCaseDetail? Detail,
    bool GpsDeferred = true);

public sealed record PayrollIntelligenceCaseDetail(
    PayrollAdminCase AdminCase,
    PayrollOverlapWorkbenchDetail? Overlap,
    PayrollMissingWorkbenchDetail? Missing,
    PayrollIntelligenceGpsContext GpsContext);

public sealed record PayrollOverlapWorkbenchDetail(
    PayrollOverlapSideCard A,
    PayrollOverlapSideCard B,
    DateTimeOffset OverlapStart,
    DateTimeOffset OverlapEnd,
    decimal OverlapHours,
    decimal OverlapMinutes,
    OverlapKind Kind,
    string KindLabelNl,
    OverlapRecommendationAction Recommendation,
    long? TargetPerformanceId,
    TimeOnly? ProposedStart,
    TimeOnly? ProposedEnd,
    string AdviceNl,
    OverlapConfidence Confidence,
    string? ImpactDeleteSummary,
    string? ImpactAdjustSummary,
    bool CanAdjustTarget,
    bool CanDeleteTarget,
    string? AdjustBlockReason,
    string? DeleteBlockReason);

public sealed record PayrollOverlapSideCard(
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHours,
    string? ProjectId,
    string? BonNr,
    string? Description,
    string? Memo,
    int? HfdTaakId,
    string? ActivityType,
    bool AdjustSupported,
    bool DeleteSupported,
    string CapabilityMessage);

public sealed record PayrollMissingWorkbenchDetail(
    MissingTechnicianTravelMode TravelMode,
    string TravelModeDutch,
    string EvidenceClass,
    string EvidenceClassLabel,
    string? PeerResourceId,
    long? PeerPerformanceId,
    TimeOnly? PeerStart,
    TimeOnly? PeerEnd,
    string? PeerIntervalSummary,
    TimeOnly? GpsSiteStart,
    TimeOnly? GpsSiteEnd,
    string? GpsSiteNote,
    TimeOnly? ProposalStart,
    TimeOnly? ProposalEnd,
    decimal? ProposalHours,
    string ProposalSource,
    string? SuggestedProjectId,
    string? SuggestedBonNr,
    int? SuggestedMainTaskId,
    bool CanProposeCreate,
    string? CreateBlockReason,
    string FindingEvidence,
    string SuggestedAction,
    MissingTechnicianSiteMatch SiteMatch = MissingTechnicianSiteMatch.LocationUnknown,
    string SiteMatchNl = "",
    MissingTechnicianConflictClass ConflictClass = MissingTechnicianConflictClass.None,
    string ConflictClassNl = "",
    string? PlannedSiteLabel = null,
    string? ExistingBookingSummary = null,
    bool IsWrongDossier = false,
    string? ReplacementPlanNl = null,
    string? WorkContinuity = null,
    string? WorkContinuityNl = null,
    string? ExcursionClass = null,
    string? ExcursionSummary = null,
    string? OperationalSite = null,
    bool AllocationReview = false,
    string? PauseNoteNl = null);

public sealed record PayrollIntelligenceGpsContext(
    bool Available,
    bool IsLoading,
    string Summary,
    IReadOnlyList<PayrollIntelligenceGpsEvent> Events);

public sealed record PayrollIntelligenceGpsEvent(
    DateTimeOffset At,
    DateTimeOffset? End,
    string Label,
    string? Detail);

public enum PayrollIntelligenceGpsCacheHint
{
    Unknown = 0,
    Unavailable = 1,
    Available = 2,
}

public sealed record PayrollIntelligenceProposeResult(
    bool Ok,
    string Message,
    Guid? ActionId,
    string? BlockReason);

public sealed record PayrollIntelligenceGpsLoadResult(
    string AdminCaseKey,
    string ResourceId,
    DateOnly Date,
    PayrollIntelligenceGpsContext GpsContext,
    bool CacheHit,
    OverlapAnalysis? OverlapWithGps,
    string? TravelModeDutch,
    string? GpsSiteSummary);

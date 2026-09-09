using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollStandbyWorkbenchPage(
    int Year,
    int Month,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> Cases,
    string? SelectedKey,
    PayrollStandbyCaseDetail? Detail,
    PayrollStandbyWorkbenchMetrics Metrics,
    IReadOnlyDictionary<string, string>? TechnicianQueuePreviews = null,
    IReadOnlyDictionary<string, string>? ClassificationQueueLabels = null,
    bool MonthContextPending = false,
    bool MonthContextReady = false);

public sealed record PayrollStandbyCaseDetail(
    PayrollAdminCase AdminCase,
    IReadOnlyList<PayrollStandbyBookedRow> BookedRows,
    string MatchingReservationLabel,
    IReadOnlyList<PayrollStandbyPlanningRow> DayPlanningRows,
    IReadOnlyList<PayrollStandbyTimelineRow> NeighborTimelineRows,
    PayrollStandbyGpsContext GpsContext,
    IReadOnlyList<PayrollStandbyCorrectionTarget> CorrectionTargets,
    IReadOnlyList<string> TechnicalCollapsedNotes,
    PayrollStandbyTechnicianContext? TechnicianContext = null,
    IReadOnlyList<PayrollStandbyDayTimelineEntry>? DayTimeline = null,
    bool HasSupportedTimeCorrection = false,
    PayrollStandbyFocusedContext? FocusedContext = null,
    PayrollStandbyAssessment? Assessment = null,
    PayrollStandbyPlanningComparison? PlanningComparison = null)
{
    public const string GpsNeverValidatesNote =
        "GPS is enkel context en valideert wachtdienst nooit automatisch.";

    public const string NoTechnicianRemarkMessage = "Geen opmerking van technieker gevonden.";

    public const string CreateGatedMessage =
        "Dit voorstel vereist het aanmaken van een tweede prestatie en kan momenteel "
        + "nog niet volledig worden uitgevoerd.";

    public decimal TotalAtlHours => BookedRows.Sum(item => item.AtlHours);
}

/// <summary>
/// Optional planning comparison when a matching reservation exists (evidence only).
/// </summary>
public sealed record PayrollStandbyPlanningComparison(
    bool HasMatchingPlanning,
    int MatchingCount,
    string BookedSummary,
    string? PlannedSummary,
    string DifferenceSummary,
    string TopSummary,
    decimal BookedHours,
    decimal? PlannedHours,
    int? DifferenceMinutes,
    TimeOnly? SuggestedAdjustStart,
    TimeOnly? SuggestedAdjustEnd);

/// <summary>
/// Deterministic standby classification + payable proposals (facts only; never auto-applies).
/// </summary>
public sealed record PayrollStandbyAssessment(
    string ClassificationLabel,
    StandbyGpsClassification Classification,
    string BookedSummary,
    string TopSummary,
    string EvidenceSummary,
    PayrollStandbyPhoneProposal? PhoneProposal,
    PayrollStandbyPhysicalProposal? PhysicalProposal,
    PayrollStandbyHybridProposal? HybridProposal,
    bool CreateExecutionGated,
    string? CreateGatedMessage,
    TimeOnly? SuggestedAdjustStart,
    TimeOnly? SuggestedAdjustEnd,
    string? SuggestedAdjustReason);

public sealed record PayrollStandbyPhoneProposal(
    TimeOnly ProposedStart,
    TimeOnly ProposedEnd,
    decimal PayableHours,
    string Summary);

public sealed record PayrollStandbyPhysicalProposal(
    TimeOnly DepartureStart,
    TimeOnly ReturnEnd,
    decimal PayableHours,
    string Summary,
    bool IsCompleteCallout);

public sealed record PayrollStandbyHybridProposal(
    PayrollStandbyPhoneProposal PhoneSegment,
    PayrollStandbyPhysicalProposal PhysicalSegment,
    int UnpaidGapMinutes,
    string GapSummary,
    bool RequiresCreateSplit,
    string? CreateGatedMessage);

/// <summary>
/// Concise default review window: VOOR / WACHTDIENST / INTERVENTIE / DAARNA. FullDay on demand.
/// </summary>
public sealed record PayrollStandbyFocusedContext(
    string? ContextSummary,
    IReadOnlyList<PayrollStandbyDayTimelineEntry> Before,
    IReadOnlyList<PayrollStandbyDayTimelineEntry> Booking,
    IReadOnlyList<PayrollStandbyDayTimelineEntry> Intervention,
    IReadOnlyList<PayrollStandbyDayTimelineEntry> After,
    IReadOnlyList<PayrollStandbyDayTimelineEntry> FullDay,
    bool HasMoreThanFocused);

public enum PayrollStandbyDayTimelineKind
{
    Gps = 0,
    Performance = 1,
    Standby = 2,
    Planning = 3,
    Intervention = 4,
}

public sealed record PayrollStandbyDayTimelineEntry(
    DateTimeOffset SortAt,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    PayrollStandbyDayTimelineKind Kind,
    string Badge,
    string Title,
    string? Subtitle,
    bool IsSelectedStandby,
    string? Locality = null,
    string? SecondaryDetail = null,
    string? GpsRelation = null);

/// <summary>
/// Distinct technician-facing text sources. Never merges BON.MEMO with PROJ_Prest fields.
/// </summary>
public sealed record PayrollStandbyTechnicianContext(
    string? BonTechnicianRemark,
    string? BonNr,
    string BonRemarkSourceField,
    IReadOnlyList<PayrollStandbyPerformanceRemark> PerformanceRemarks,
    bool HasAnyTechnicianText)
{
    public const string BonMemoSourceField = "BON.MEMO";
}

public sealed record PayrollStandbyPerformanceRemark(
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHours,
    string? PrestOmschr,
    string? PrestMemo,
    bool ShowPrestMemo);

public sealed record PayrollStandbyBookedRow(
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHours,
    string? Description,
    string? Memo,
    string? ProjectId,
    string? BonNr,
    int? HfdTaakId,
    bool IsSelected,
    string? ProjectDisplayLabel = null,
    int? ProjectNumber = null);

public sealed record PayrollStandbyPlanningRow(
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    string Label,
    PayrollPlanningClassification Classification,
    bool IsMatchingSupport);

public enum PayrollStandbyTimelineKind
{
    Previous = 0,
    Selected = 1,
    Next = 2,
    Other = 3,
}

public sealed record PayrollStandbyTimelineRow(
    PayrollStandbyTimelineKind Kind,
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? ProjectLabel,
    string? Description,
    bool IsSelectedStandby);

public sealed record PayrollStandbyGpsEvent(
    DateTimeOffset At,
    DateTimeOffset? End,
    string Label,
    string? Detail,
    string Phase = "");

public sealed record PayrollStandbyGpsContext(
    bool Available,
    string Summary,
    IReadOnlyList<PayrollStandbyGpsEvent> Events,
    string MappingKind,
    string? ObjectIdCollapsed,
    IReadOnlyList<StandbyGpsTripEvidence> TripsCollapsed,
    bool IsLoading = false,
    string? CacheStatus = null);

public enum PayrollStandbyCorrectionCapability
{
    SupportedVanTot = 0,
    UnsupportedActivity = 1,
    /// <summary>Legacy: previously always appended; prefer <see cref="SupportedDelete"/>.</summary>
    ZeroDeleteUnavailable = 2,
    SupportedDelete = 3,
}

public sealed record PayrollStandbyCorrectionTarget(
    long PerformanceId,
    DateTimeOffset? CurrentStart,
    DateTimeOffset? CurrentEnd,
    decimal AtlHours,
    int? HfdTaakId,
    string? ActivityType,
    PayrollStandbyCorrectionCapability CorrectionCapability,
    string CapabilityMessage,
    string? FriendlyTaskName = null,
    TimeOnly? SuggestedAdjustStart = null,
    TimeOnly? SuggestedAdjustEnd = null);

public sealed record PayrollStandbyWorkbenchMetrics(
    int PlenionPerformanceQueries,
    int PlenionPlanningQueries,
    int PowerFleetApiCalls,
    bool GpsDeferred = false,
    bool GpsCacheHit = false,
    bool MonthContextHit = false,
    int BonQueries = 0,
    long? MonthContextBuildMs = null,
    long? SelectionBuildMs = null);

/// <summary>
/// Pre-resolved PWS activity for a concrete PerformanceId (no invented ID map).
/// </summary>
public sealed record PayrollStandbyResolvedActivity(
    long PerformanceId,
    string? ActivityType,
    bool Supported,
    string Message,
    string? FriendlyTaskName);

public enum PayrollStandbyGpsCacheHint
{
    Unknown = 0,
    Cached = 1,
    Loading = 2,
    Unavailable = 3,
    Available = 4,
}

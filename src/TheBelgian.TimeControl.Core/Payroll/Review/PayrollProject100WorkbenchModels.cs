using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollProject100WorkbenchPage(
    int Year,
    int Month,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> Cases,
    string? SelectedKey,
    PayrollProject100CaseDetail? Detail,
    PayrollProject100WorkbenchMetrics Metrics,
    IReadOnlyDictionary<string, string>? TechnicianQueuePreviews = null,
    bool MonthContextPending = false,
    bool MonthContextReady = false);

public sealed record PayrollProject100CaseDetail(
    PayrollAdminCase AdminCase,
    IReadOnlyList<PayrollProject100BookedRow> BookedRows,
    string MatchingReservationLabel,
    IReadOnlyList<PayrollProject100PlanningRow> DayPlanningRows,
    IReadOnlyList<PayrollProject100TimelineRow> NeighborTimelineRows,
    PayrollProject100GpsContext GpsContext,
    IReadOnlyList<PayrollProject100CorrectionTarget> CorrectionTargets,
    IReadOnlyList<string> TechnicalCollapsedNotes,
    PayrollProject100TechnicianContext? TechnicianContext = null,
    IReadOnlyList<PayrollProject100DayTimelineEntry>? DayTimeline = null,
    bool HasSupportedTimeCorrection = false,
    PayrollProject100FocusedContext? FocusedContext = null,
    PayrollProject100PlanningComparison? PlanningComparison = null,
    PayrollProject100PeerEvidence? PeerEvidence = null,
    PayrollProject100DayTotals? DayTotals = null)
{
    public const string GpsNeverValidatesNote =
        "GPS is enkel context en valideert opleiding nooit automatisch.";

    public const string NoTechnicianRemarkMessage = "Geen opmerking van technieker gevonden.";

    public decimal TotalAtlHours => BookedRows.Sum(item => item.AtlHours);
}

/// <summary>
/// Peer bookings for the same Project100 / toolbox session (facts for human review).
/// </summary>
public sealed record PayrollProject100PeerEvidence(
    int PeerCount,
    string? TypicalIntervalSummary,
    int TypicalCount,
    string AdminSummary,
    IReadOnlyList<PayrollProject100PeerBooking> Bookings,
    TimeOnly? SuggestedAdjustStart,
    TimeOnly? SuggestedAdjustEnd);

public sealed record PayrollProject100PeerBooking(
    string ResourceId,
    TimeOnly? Start,
    TimeOnly? End,
    decimal Hours,
    long PerformanceId);

/// <summary>
/// Daily totals for training overtime review. Training never creates payable overtime.
/// </summary>
public sealed record PayrollProject100DayTotals(
    decimal OtherWorkHours,
    decimal TrainingHours,
    decimal TheoreticalDayHours,
    decimal PotentialOrdinaryOvertimeBeforeTrainingRule,
    decimal PayableOvertimeAttributableToTraining,
    string TheoreticalDayLabel);

/// <summary>
/// Admin-facing booked vs planned comparison for Project 100 (facts only).
/// </summary>
public sealed record PayrollProject100PlanningComparison(
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
/// Concise default review window: before / booking / after. FullDay remains available on demand.
/// </summary>
public sealed record PayrollProject100FocusedContext(
    string? ContextSummary,
    IReadOnlyList<PayrollProject100DayTimelineEntry> Before,
    IReadOnlyList<PayrollProject100DayTimelineEntry> Booking,
    IReadOnlyList<PayrollProject100DayTimelineEntry> After,
    IReadOnlyList<PayrollProject100DayTimelineEntry> FullDay,
    bool HasMoreThanFocused);

public enum PayrollProject100DayTimelineKind
{
    Gps = 0,
    Performance = 1,
    Project100 = 2,
    Planning = 3,
}

public sealed record PayrollProject100DayTimelineEntry(
    DateTimeOffset SortAt,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    PayrollProject100DayTimelineKind Kind,
    string Badge,
    string Title,
    string? Subtitle,
    bool IsSelected100,
    string? Locality = null,
    string? SecondaryDetail = null,
    string? GpsRelation = null);

/// <summary>
/// Distinct technician-facing text sources. Never merges BON.MEMO with PROJ_Prest fields.
/// </summary>
public sealed record PayrollProject100TechnicianContext(
    string? BonTechnicianRemark,
    string? BonNr,
    string BonRemarkSourceField,
    IReadOnlyList<PayrollProject100PerformanceRemark> PerformanceRemarks,
    bool HasAnyTechnicianText)
{
    public const string BonMemoSourceField = "BON.MEMO";
}

public sealed record PayrollProject100PerformanceRemark(
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHours,
    string? PrestOmschr,
    string? PrestMemo,
    bool ShowPrestMemo);


public sealed record PayrollProject100BookedRow(
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

public sealed record PayrollProject100PlanningRow(
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    string Label,
    PayrollPlanningClassification Classification,
    bool IsMatchingSupport);

public enum PayrollProject100TimelineKind
{
    Previous = 0,
    Selected = 1,
    Next = 2,
    Other = 3,
}

public sealed record PayrollProject100TimelineRow(
    PayrollProject100TimelineKind Kind,
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? ProjectLabel,
    string? Description,
    bool IsSelected100);

public sealed record PayrollProject100GpsEvent(
    DateTimeOffset At,
    DateTimeOffset? End,
    string Label,
    string? Detail,
    string Phase = "");

public sealed record PayrollProject100GpsContext(
    bool Available,
    string Summary,
    IReadOnlyList<PayrollProject100GpsEvent> Events,
    string MappingKind,
    string? ObjectIdCollapsed,
    IReadOnlyList<StandbyGpsTripEvidence> TripsCollapsed,
    bool IsLoading = false,
    string? CacheStatus = null);

public enum PayrollProject100CorrectionCapability
{
    SupportedVanTot = 0,
    UnsupportedActivity = 1,
    /// <summary>Legacy: previously always appended; prefer <see cref="SupportedDelete"/>.</summary>
    ZeroDeleteUnavailable = 2,
    SupportedDelete = 3,
}

public sealed record PayrollProject100CorrectionTarget(
    long PerformanceId,
    DateTimeOffset? CurrentStart,
    DateTimeOffset? CurrentEnd,
    decimal AtlHours,
    int? HfdTaakId,
    string? ActivityType,
    PayrollProject100CorrectionCapability CorrectionCapability,
    string CapabilityMessage,
    string? FriendlyTaskName = null,
    TimeOnly? SuggestedAdjustStart = null,
    TimeOnly? SuggestedAdjustEnd = null);

public sealed record PayrollProject100WorkbenchMetrics(
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
public sealed record PayrollProject100ResolvedActivity(
    long PerformanceId,
    string? ActivityType,
    bool Supported,
    string Message,
    string? FriendlyTaskName);

public enum PayrollProject100GpsCacheHint
{
    Unknown = 0,
    Cached = 1,
    Loading = 2,
    Unavailable = 3,
    Available = 4,
}

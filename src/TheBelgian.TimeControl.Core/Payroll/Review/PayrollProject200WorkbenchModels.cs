using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollProject200WorkbenchPage(
    int Year,
    int Month,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> Cases,
    string? SelectedKey,
    PayrollProject200CaseDetail? Detail,
    PayrollProject200WorkbenchMetrics Metrics,
    IReadOnlyDictionary<string, string>? TechnicianQueuePreviews = null,
    bool MonthContextPending = false,
    bool MonthContextReady = false);

public sealed record PayrollProject200CaseDetail(
    PayrollAdminCase AdminCase,
    IReadOnlyList<PayrollProject200BookedRow> BookedRows,
    string MatchingReservationLabel,
    IReadOnlyList<PayrollProject200PlanningRow> DayPlanningRows,
    IReadOnlyList<PayrollProject200TimelineRow> NeighborTimelineRows,
    PayrollProject200GpsContext GpsContext,
    IReadOnlyList<PayrollProject200CorrectionTarget> CorrectionTargets,
    IReadOnlyList<string> TechnicalCollapsedNotes,
    PayrollProject200TechnicianContext? TechnicianContext = null,
    IReadOnlyList<PayrollProject200DayTimelineEntry>? DayTimeline = null,
    bool HasSupportedTimeCorrection = false,
    PayrollProject200FocusedContext? FocusedContext = null,
    PayrollProject200PlanningComparison? PlanningComparison = null)
{
    public const string GpsNeverValidatesNote =
        "GPS is enkel context en valideert Project 200 nooit automatisch.";

    public const string NoTechnicianRemarkMessage = "Geen opmerking van technieker gevonden.";

    public decimal TotalAtlHours => BookedRows.Sum(item => item.AtlHours);
}

/// <summary>
/// Admin-facing booked vs planned comparison for Project 200 (facts only).
/// </summary>
public sealed record PayrollProject200PlanningComparison(
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
public sealed record PayrollProject200FocusedContext(
    string? ContextSummary,
    IReadOnlyList<PayrollProject200DayTimelineEntry> Before,
    IReadOnlyList<PayrollProject200DayTimelineEntry> Booking,
    IReadOnlyList<PayrollProject200DayTimelineEntry> After,
    IReadOnlyList<PayrollProject200DayTimelineEntry> FullDay,
    bool HasMoreThanFocused);

public enum PayrollProject200DayTimelineKind
{
    Gps = 0,
    Performance = 1,
    Project200 = 2,
    Planning = 3,
}

public sealed record PayrollProject200DayTimelineEntry(
    DateTimeOffset SortAt,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    PayrollProject200DayTimelineKind Kind,
    string Badge,
    string Title,
    string? Subtitle,
    bool IsSelected200,
    string? Locality = null,
    string? SecondaryDetail = null,
    string? GpsRelation = null);

/// <summary>
/// Distinct technician-facing text sources. Never merges BON.MEMO with PROJ_Prest fields.
/// </summary>
public sealed record PayrollProject200TechnicianContext(
    string? BonTechnicianRemark,
    string? BonNr,
    string BonRemarkSourceField,
    IReadOnlyList<PayrollProject200PerformanceRemark> PerformanceRemarks,
    bool HasAnyTechnicianText)
{
    public const string BonMemoSourceField = "BON.MEMO";
}

public sealed record PayrollProject200PerformanceRemark(
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHours,
    string? PrestOmschr,
    string? PrestMemo,
    bool ShowPrestMemo);


public sealed record PayrollProject200BookedRow(
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

public sealed record PayrollProject200PlanningRow(
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    string Label,
    PayrollPlanningClassification Classification,
    bool IsMatchingSupport);

public enum PayrollProject200TimelineKind
{
    Previous = 0,
    Selected = 1,
    Next = 2,
    Other = 3,
}

public sealed record PayrollProject200TimelineRow(
    PayrollProject200TimelineKind Kind,
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? ProjectLabel,
    string? Description,
    bool IsSelected200);

public sealed record PayrollProject200GpsEvent(
    DateTimeOffset At,
    DateTimeOffset? End,
    string Label,
    string? Detail,
    string Phase = "");

public sealed record PayrollProject200GpsContext(
    bool Available,
    string Summary,
    IReadOnlyList<PayrollProject200GpsEvent> Events,
    string MappingKind,
    string? ObjectIdCollapsed,
    IReadOnlyList<StandbyGpsTripEvidence> TripsCollapsed,
    bool IsLoading = false,
    string? CacheStatus = null);

public enum PayrollProject200CorrectionCapability
{
    SupportedVanTot = 0,
    UnsupportedActivity = 1,
    /// <summary>Legacy: previously always appended; prefer <see cref="SupportedDelete"/>.</summary>
    ZeroDeleteUnavailable = 2,
    SupportedDelete = 3,
}

public sealed record PayrollProject200CorrectionTarget(
    long PerformanceId,
    DateTimeOffset? CurrentStart,
    DateTimeOffset? CurrentEnd,
    decimal AtlHours,
    int? HfdTaakId,
    string? ActivityType,
    PayrollProject200CorrectionCapability CorrectionCapability,
    string CapabilityMessage,
    string? FriendlyTaskName = null,
    TimeOnly? SuggestedAdjustStart = null,
    TimeOnly? SuggestedAdjustEnd = null);

public sealed record PayrollProject200WorkbenchMetrics(
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
public sealed record PayrollProject200ResolvedActivity(
    long PerformanceId,
    string? ActivityType,
    bool Supported,
    string Message,
    string? FriendlyTaskName);

public enum PayrollProject200GpsCacheHint
{
    Unknown = 0,
    Cached = 1,
    Loading = 2,
    Unavailable = 3,
    Available = 4,
}

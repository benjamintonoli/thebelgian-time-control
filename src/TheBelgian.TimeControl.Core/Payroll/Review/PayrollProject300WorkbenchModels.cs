using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollProject300WorkbenchPage(
    int Year,
    int Month,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> Cases,
    string? SelectedKey,
    PayrollProject300CaseDetail? Detail,
    PayrollProject300WorkbenchMetrics Metrics,
    IReadOnlyDictionary<string, string>? TechnicianQueuePreviews = null,
    bool MonthContextPending = false,
    bool MonthContextReady = false);

public sealed record PayrollProject300CaseDetail(
    PayrollAdminCase AdminCase,
    IReadOnlyList<PayrollProject300BookedRow> BookedRows,
    string MatchingReservationLabel,
    IReadOnlyList<PayrollProject300PlanningRow> DayPlanningRows,
    IReadOnlyList<PayrollProject300TimelineRow> NeighborTimelineRows,
    PayrollProject300GpsContext GpsContext,
    IReadOnlyList<PayrollProject300CorrectionTarget> CorrectionTargets,
    IReadOnlyList<string> TechnicalCollapsedNotes,
    PayrollProject300TechnicianContext? TechnicianContext = null,
    IReadOnlyList<PayrollProject300DayTimelineEntry>? DayTimeline = null,
    bool HasSupportedTimeCorrection = false,
    PayrollProject300FocusedContext? FocusedContext = null)
{
    public const string GpsNeverValidatesNote =
        "GPS is enkel context en valideert Project 300 nooit automatisch.";

    public const string NoTechnicianRemarkMessage = "Geen opmerking van technieker gevonden.";

    public decimal TotalAtlHours => BookedRows.Sum(item => item.AtlHours);
}

/// <summary>
/// Concise default review window: before / booking / after. FullDay remains available on demand.
/// </summary>
public sealed record PayrollProject300FocusedContext(
    string? ContextSummary,
    IReadOnlyList<PayrollProject300DayTimelineEntry> Before,
    IReadOnlyList<PayrollProject300DayTimelineEntry> Booking,
    IReadOnlyList<PayrollProject300DayTimelineEntry> After,
    IReadOnlyList<PayrollProject300DayTimelineEntry> FullDay,
    bool HasMoreThanFocused);

public enum PayrollProject300DayTimelineKind
{
    Gps = 0,
    Performance = 1,
    Project300 = 2,
    Planning = 3,
}

public sealed record PayrollProject300DayTimelineEntry(
    DateTimeOffset SortAt,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    PayrollProject300DayTimelineKind Kind,
    string Badge,
    string Title,
    string? Subtitle,
    bool IsSelected300,
    string? Locality = null,
    string? SecondaryDetail = null,
    string? GpsRelation = null);

/// <summary>
/// Distinct technician-facing text sources. Never merges BON.MEMO with PROJ_Prest fields.
/// </summary>
public sealed record PayrollProject300TechnicianContext(
    string? BonTechnicianRemark,
    string? BonNr,
    string BonRemarkSourceField,
    IReadOnlyList<PayrollProject300PerformanceRemark> PerformanceRemarks,
    bool HasAnyTechnicianText)
{
    public const string BonMemoSourceField = "BON.MEMO";
}

public sealed record PayrollProject300PerformanceRemark(
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHours,
    string? PrestOmschr,
    string? PrestMemo,
    bool ShowPrestMemo);


public sealed record PayrollProject300BookedRow(
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

public sealed record PayrollProject300PlanningRow(
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    string Label,
    PayrollPlanningClassification Classification,
    bool IsMatchingSupport);

public enum PayrollProject300TimelineKind
{
    Previous = 0,
    Selected = 1,
    Next = 2,
    Other = 3,
}

public sealed record PayrollProject300TimelineRow(
    PayrollProject300TimelineKind Kind,
    long PerformanceId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? ProjectLabel,
    string? Description,
    bool IsSelected300);

public sealed record PayrollProject300GpsEvent(
    DateTimeOffset At,
    DateTimeOffset? End,
    string Label,
    string? Detail,
    string Phase = "");

public sealed record PayrollProject300GpsContext(
    bool Available,
    string Summary,
    IReadOnlyList<PayrollProject300GpsEvent> Events,
    string MappingKind,
    string? ObjectIdCollapsed,
    IReadOnlyList<StandbyGpsTripEvidence> TripsCollapsed,
    bool IsLoading = false,
    string? CacheStatus = null);

public enum PayrollProject300CorrectionCapability
{
    SupportedVanTot = 0,
    UnsupportedActivity = 1,
    ZeroDeleteUnavailable = 2,
}

public sealed record PayrollProject300CorrectionTarget(
    long PerformanceId,
    DateTimeOffset? CurrentStart,
    DateTimeOffset? CurrentEnd,
    decimal AtlHours,
    int? HfdTaakId,
    string? ActivityType,
    PayrollProject300CorrectionCapability CorrectionCapability,
    string CapabilityMessage,
    string? FriendlyTaskName = null);

public sealed record PayrollProject300WorkbenchMetrics(
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
public sealed record PayrollProject300ResolvedActivity(
    long PerformanceId,
    string? ActivityType,
    bool Supported,
    string Message,
    string? FriendlyTaskName);

public enum PayrollProject300GpsCacheHint
{
    Unknown = 0,
    Cached = 1,
    Loading = 2,
    Unavailable = 3,
    Available = 4,
}

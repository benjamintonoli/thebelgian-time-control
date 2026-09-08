using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollProject300WorkbenchPage(
    int Year,
    int Month,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> Cases,
    string? SelectedKey,
    PayrollProject300CaseDetail? Detail,
    PayrollProject300WorkbenchMetrics Metrics);

public sealed record PayrollProject300CaseDetail(
    PayrollAdminCase AdminCase,
    IReadOnlyList<PayrollProject300BookedRow> BookedRows,
    string MatchingReservationLabel,
    IReadOnlyList<PayrollProject300PlanningRow> DayPlanningRows,
    IReadOnlyList<PayrollProject300TimelineRow> NeighborTimelineRows,
    PayrollProject300GpsContext GpsContext,
    IReadOnlyList<PayrollProject300CorrectionTarget> CorrectionTargets,
    IReadOnlyList<string> TechnicalCollapsedNotes)
{
    public const string GpsNeverValidatesNote =
        "GPS is enkel context en valideert Project 300 nooit automatisch.";

    public decimal TotalAtlHours => BookedRows.Sum(item => item.AtlHours);
}

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
    bool IsSelected);

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
    string? Detail);

public sealed record PayrollProject300GpsContext(
    bool Available,
    string Summary,
    IReadOnlyList<PayrollProject300GpsEvent> Events,
    string MappingKind,
    string? ObjectIdCollapsed,
    IReadOnlyList<StandbyGpsTripEvidence> TripsCollapsed);

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
    string CapabilityMessage);

public sealed record PayrollProject300WorkbenchMetrics(
    int PlenionPerformanceQueries,
    int PlenionPlanningQueries,
    int PowerFleetApiCalls);

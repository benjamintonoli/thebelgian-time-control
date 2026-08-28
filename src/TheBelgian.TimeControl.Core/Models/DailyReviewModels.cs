namespace TheBelgian.TimeControl.Core.Models;

public enum DailyReviewWorkflowStatus
{
    Open = 0,
    ResolvedNoAction = 1,
    PendingCorrection = 2,
    AwaitingExplanation = 3,
    EscalatedForManagementReview = 4,
    NeedsReReview = 5,
    CorrectionExecuted = 6,
}

public enum DailyReviewEvidenceLevel
{
    Complete = 0,
    Partial = 1,
    Insufficient = 2,
}

public enum DailyReviewQueueView
{
    Open = 0,
    ToReview = 1,
    Completed = 2,
    All = 3,
    DataQuality = 4,
    NotApplicable = 5,
}

public enum DailyReviewBoundaryFilter
{
    All = 0,
    Start = 1,
    End = 2,
}

public enum DailyReviewSort
{
    LargestDifference = 0,
    DateAscending = 1,
    DateDescending = 2,
    Technician = 3,
}

public enum ReviewFeedbackReason
{
    CorrectRegistration = 0,
    AdministrativeEntryError = 1,
    AlternativeWorkLocation = 2,
    SharedVehicle = 3,
    WrongVehicleAssignment = 4,
    GpsIssue = 5,
    LargeCampus = 6,
    ExplanationAccepted = 7,
    UnexplainedMismatch = 8,
    Other = 9,
}

public sealed record DailyReviewBoundaryEvidence(
    string Side,
    long PerformanceId,
    string? Customer,
    string? Address,
    DateTimeOffset PlenionTime,
    DateTimeOffset? GpsTime,
    double? SignedDifferenceMinutes,
    bool IsReliable,
    string EvidenceType,
    string MatcherStatus,
    double? Score,
    double? DistanceMeters,
    int? OverlapMinutes,
    string? SelectedVisitId,
    string? TechnicalReason);

public sealed record DailyReviewDecision(
    DailyReviewWorkflowStatus Status,
    ReviewFeedbackReason? Reason,
    string? Notes,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ProposedStart,
    DateTimeOffset? ProposedEnd);

public sealed record DailyReviewCase(
    string CaseId,
    string? TechnicianId,
    string Technician,
    DateOnly Date,
    DailyReviewBoundaryEvidence First,
    DailyReviewBoundaryEvidence Last,
    DailyReviewEvidenceLevel EvidenceLevel,
    string AuditReviewStatus,
    string AlgorithmVersion,
    DateTimeOffset CreatedAt,
    string EvidenceSnapshotJson,
    DailyReviewDecision Decision,
    DailyReviewTripContext? TripContext = null)
{
    public double TotalPositiveMinutes =>
        Math.Max(0, First.SignedDifferenceMinutes ?? 0) +
        Math.Max(0, Last.SignedDifferenceMinutes ?? 0);

    public double MaximumAbsoluteDifferenceMinutes => Math.Max(
        Math.Abs(First.SignedDifferenceMinutes ?? 0),
        Math.Abs(Last.SignedDifferenceMinutes ?? 0));

    public string Customer => First.Customer ?? Last.Customer ?? "—";
    public string Address => First.Address ?? Last.Address ?? "—";

    public double ConfirmedPositiveMinutes =>
        (First.IsReliable ? Math.Max(0, First.SignedDifferenceMinutes ?? 0) : 0) +
        (Last.IsReliable ? Math.Max(0, Last.SignedDifferenceMinutes ?? 0) : 0);
}

public sealed record DailyReviewTrip(
    string TripId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string StartAddress,
    string EndAddress,
    double? DistanceKilometres,
    bool DistanceIsEstimated,
    bool IsFirstBoundaryArrivalTrip,
    bool IsLastBoundaryDepartureTrip)
{
    public int DurationMinutes => Math.Max(0, (int)Math.Round(
        (End - Start).TotalMinutes, MidpointRounding.AwayFromZero));
}

public sealed record DailyReviewTripContext(
    DailyReviewTrip? TripBeforeFirstCustomer,
    DailyReviewTrip? TripAfterLastCustomer,
    IReadOnlyList<DailyReviewTrip> DayTrips);

public sealed record DailyReviewFilter(
    DailyReviewQueueView View = DailyReviewQueueView.Open,
    string? Technician = null,
    DateOnly? Date = null,
    DailyReviewBoundaryFilter Boundary = DailyReviewBoundaryFilter.All,
    DailyReviewEvidenceLevel? Evidence = null,
    bool EscalatedOnly = false,
    DailyReviewSort Sort = DailyReviewSort.LargestDifference);

public sealed record DailyReviewCounts(
    int Open,
    int ToReview,
    int Completed,
    int Total,
    int DataQuality = 0,
    int NotApplicable = 0);

public sealed record DailyReviewCockpit(
    IReadOnlyList<DailyReviewCase> Cases,
    DailyReviewCase? Selected,
    IReadOnlyList<DailyReviewCase> RecentCases,
    DailyReviewCounts Counts);

public sealed record SaveDailyReviewDecision(
    string CaseId,
    DailyReviewWorkflowStatus Status,
    ReviewFeedbackReason? Reason,
    string Reviewer,
    string? Notes,
    DateTimeOffset? ProposedStart,
    DateTimeOffset? ProposedEnd);

public sealed class DailyReviewActionAudit
{
    public long Id { get; set; }
    public required string CaseId { get; set; }
    public required string Technician { get; set; }
    public DateOnly Date { get; set; }
    public required string Decision { get; set; }
    public string? DecisionReason { get; set; }
    public string? Notes { get; set; }
    public required string ReviewedBy { get; set; }
    public DateTimeOffset ReviewedAt { get; set; }
    public required string EvidenceSnapshotJson { get; set; }
    public required string AlgorithmVersion { get; set; }
}

public sealed class DailyCorrectionProposal
{
    public long Id { get; set; }
    public required string CaseId { get; set; }
    public DateTimeOffset OriginalStart { get; set; }
    public DateTimeOffset OriginalEnd { get; set; }
    public DateTimeOffset? ProposedStart { get; set; }
    public DateTimeOffset? ProposedEnd { get; set; }
    public required string Reason { get; set; }
    public string? Notes { get; set; }
    public required string ProposedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Status { get; set; }
    public long FirstPerformanceId { get; set; }
    public long LastPerformanceId { get; set; }
    public string FirstActivityType { get; set; } = string.Empty;
    public string LastActivityType { get; set; } = string.Empty;
    public long? FirstMainTaskExternalId { get; set; }
    public long? LastMainTaskExternalId { get; set; }
    public DateTimeOffset? FirstRecordOriginalStart { get; set; }
    public DateTimeOffset? FirstRecordOriginalEnd { get; set; }
    public DateTimeOffset? LastRecordOriginalStart { get; set; }
    public DateTimeOffset? LastRecordOriginalEnd { get; set; }
    public DateTimeOffset? ExecutedStart { get; set; }
    public DateTimeOffset? ExecutedEnd { get; set; }
    public string? ExecutedBy { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? PlenionWriteReference { get; set; }
    public string? PlenionWriteResponse { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class CorrectionProposalStatuses
{
    public const string Proposed = "Proposed";
    public const string Approved = "Approved";
    public const string Executing = "Executing";
    public const string Executed = "Executed";
    public const string Conflict = "Conflict";
    public const string Failed = "Failed";
    public const string WriteVerificationFailed = "WriteVerificationFailed";
}

public sealed record CorrectionExecutionAvailability(bool Enabled, bool Reachable, string Message)
{
    public bool CanExecute => Enabled && Reachable;
}

public sealed record CorrectionExecutionResult(
    string Status,
    string Message,
    DailyCorrectionProposal Proposal);

public sealed record ExecuteDirectCorrectionRequest(
    string CaseId,
    ReviewFeedbackReason Reason,
    string Reviewer,
    string? Notes,
    DateTimeOffset? ProposedStart,
    DateTimeOffset? ProposedEnd);

public sealed class DailyGeneratedFactualReport
{
    public long Id { get; set; }
    public required string Technician { get; set; }
    public required string CaseIdsJson { get; set; }
    public required string Content { get; set; }
    public required string GeneratedBy { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed record GeneratedFactualReport(long Id, string FileName, string Content);

public enum MonthlyReviewStatus
{
    WaitingForData = 0,
    ReadyForReview = 1,
    InReview = 2,
    Finalized = 3,
}

public sealed class MonthlyReviewPeriod
{
    public long Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public MonthlyReviewStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PreparedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? FinalizedBy { get; set; }
    public string AlgorithmVersion { get; set; } = string.Empty;
    public DateTimeOffset? SourceCutoffAt { get; set; }
    public DateTimeOffset? LastVehicleSyncAt { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public string? FinalSnapshotJson { get; set; }
}

public sealed class MonthlyReviewCaseSnapshot
{
    public long Id { get; set; }
    public long MonthlyReviewPeriodId { get; set; }
    public string CaseId { get; set; } = string.Empty;
    public string Technician { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string EvidenceHash { get; set; } = string.Empty;
    public string EvidenceSnapshotJson { get; set; } = string.Empty;
    public string CaseJson { get; set; } = string.Empty;
    public string? PreviousEvidenceSnapshotJson { get; set; }
    public bool NeedsReReview { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record ReviewMonth(int Year, int Month)
{
    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => new(Year, Month, DateTime.DaysInMonth(Year, Month));
    public string Key => $"{Year:0000}-{Month:00}";
}

public sealed record MonthlyReviewSummary(
    int Workdays,
    int AssessableDays,
    int DataQualityCases,
    int NoTrackAndTrace,
    int Deviations,
    int DeviationsOver5,
    int DeviationsOver15,
    int DeviationsOver30,
    int ConfirmedPositiveMinutes,
    int CorrectionProposals = 0);

public sealed record MonthlyReviewCockpit(
    MonthlyReviewPeriod Period,
    DailyReviewCockpit Review,
    MonthlyReviewSummary Summary,
    ReviewMonth PreviousMonth,
    ReviewMonth NextMonth);

public sealed record MonthlyPrepareResult(
    MonthlyReviewPeriod Period,
    int Cases,
    int NewCases,
    int ChangedCases,
    int UnchangedCases,
    string EvidenceSource);

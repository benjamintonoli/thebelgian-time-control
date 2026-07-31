namespace TheBelgian.TimeControl.Core.Models;

/// <summary>
/// Application-wide policy: matcher output is never automatically final.
/// Does not change matching thresholds or algorithms.
/// </summary>
public enum MatcherUsageMode
{
    HumanReviewRequired = 0,
}

public static class MatcherUsagePolicy
{
    public const MatcherUsageMode CurrentMode = MatcherUsageMode.HumanReviewRequired;
    public const string HoldoutDecision = "NO-GO";
    public const bool AutomaticAcceptanceAllowed = false;
    public const bool PlenionWritebackAllowed = false;

    public const string BannerTitle = "Menselijke bevestiging verplicht";

    public const string BannerBody =
        "De tool doet voorstellen en voert geen automatische correcties uit.";
}

public enum AdminReviewStatus
{
    Pending = 0,
    Confirmed = 1,
    Rejected = 2,
    NeedsMoreInformation = 3,
    NoReliableMatch = 4,
}

public enum SpotcheckPriorityTier
{
    Informational = 0,
    SmallDeviation = 1,
    IndividualException = 2,
    HighPriority = 3,
}

/// <summary>Evidence strength is independent from time-impact priority.</summary>
public enum EvidenceStrength
{
    StrongProposal = 0,
    ProbableVisit = 1,
    MultipleCandidates = 2,
    NoReliableMatch = 3,
}

public enum ReviewWorkCategory
{
    ActionableDeviation = 0,
    SmallDeviation = 1,
    MatchUncertainty = 2,
    DataQuality = 3,
    Informational = 4,
    Completed = 5,
}

public enum ReviewWorkTab
{
    Exceptions = 0,
    SmallDeviations = 1,
    MatchUncertainty = 2,
    DataQuality = 3,
    Completed = 4,
}

/// <summary>Plenion / source facts. Never overwritten by admin decisions.</summary>
public sealed record SourceEvidence(
    long PerformanceId,
    DateOnly Date,
    string Technician,
    DateTimeOffset PlenionStart,
    DateTimeOffset PlenionEnd,
    string PlenionAddress,
    string? ProjectContext,
    string? BonContext,
    string? CustomerContext,
    string? PreviousPerformance,
    string? NextPerformance,
    string? Lacleunik);

/// <summary>One visit candidate from the matcher (proposal surface only).</summary>
public sealed record ReviewVisitCandidate(
    string VisitCandidateId,
    IReadOnlyList<string> ConstituentStopIds,
    string? Address,
    DateTimeOffset Arrival,
    DateTimeOffset Departure,
    double? DistanceMeters,
    int OverlapMinutes,
    double OverlapPercent,
    int StartDeviationMinutes,
    int EndDeviationMinutes,
    string? GeocodeQuality);

/// <summary>Original matcher outcome. Never overwritten by admin decisions.</summary>
public sealed record MatcherAssessment(
    string MatcherStatus,
    bool ProposedAcceptance,
    ReviewVisitCandidate? ProposedVisit,
    IReadOnlyList<ReviewVisitCandidate> CandidateVisits,
    string MatchReason,
    GeocodeQualityClass GeocodeQuality,
    int? StartDeviationMinutes,
    int? EndDeviationMinutes,
    int? MaxDeviationMinutes,
    string MatcherCommit,
    string ConfigurationHash);

/// <summary>Current admin decision overlay. History lives in append-only audit.</summary>
public sealed record AdminDecision(
    AdminReviewStatus Status,
    string? Reviewer = null,
    string? Comment = null,
    string? ChosenVisitCandidateId = null,
    IReadOnlyList<string>? ChosenVisitSourceStopIds = null);

/// <summary>
/// Application review case with strict separation of source, matcher, and admin layers.
/// </summary>
public sealed record ReviewCase(
    SourceEvidence Source,
    MatcherAssessment Matcher,
    AdminDecision Admin,
    SpotcheckPriorityTier? Priority,
    ReviewWorkCategory Category,
    bool HasRecurringConfirmedPattern,
    IReadOnlyList<string> SourceProvenance,
    string? DeterministicExplanation = null)
{
    public long PerformanceId => Source.PerformanceId;
    public DateOnly Date => Source.Date;
    public string Technician => Source.Technician;
    public AdminReviewStatus ReviewStatus => Admin.Status;
    public string MatcherStatus => Matcher.MatcherStatus;
    public int? MaxDeviationMinutes => Matcher.MaxDeviationMinutes;
    public bool MatcherProposedAcceptance => Matcher.ProposedAcceptance;
    public string DetailPath => $"/Admin/Reviews/{PerformanceId}";
}

/// <summary>
/// Append-only admin decision audit row. Never overwrites matcher source outcomes.
/// </summary>
public sealed class AdminReviewDecisionAudit
{
    public long Id { get; set; }
    public long PerformanceId { get; set; }
    public required string OriginalMatcherStatus { get; set; }
    public string? ProposedVisitCandidateId { get; set; }
    public string? ProposedVisitSourceStopIdsJson { get; set; }
    public string? ChosenVisitCandidateId { get; set; }
    public string? ChosenVisitSourceStopIdsJson { get; set; }
    public required string Decision { get; set; }
    public string? ReasonOrComment { get; set; }
    public required string Reviewer { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public required string MatcherCommit { get; set; }
    public required string ConfigurationHash { get; set; }
}

public sealed record AdminReviewFilter(
    ReviewWorkTab? Tab = null,
    ReviewWorkCategory? Category = null,
    string? Technician = null,
    DateOnly? FromDate = null,
    DateOnly? ThroughDate = null,
    AdminReviewStatus? ReviewStatus = null,
    string? MatcherStatus = null,
    int? MinimumDeviationMinutes = null,
    bool HighPriorityOnly = false,
    int Page = 1,
    int PageSize = 25);

public sealed record AdminReviewCategoryCounts(
    int OpenOutstanding,
    int Exceptions,
    int SmallDeviation,
    int MatchUncertainty,
    int DataQuality,
    int Completed,
    int Informational);

public sealed record AdminReviewSearchResult(
    IReadOnlyList<ReviewCase> Items,
    int TotalMatching,
    int Page,
    int PageSize,
    AdminReviewCategoryCounts Counts,
    int UniqueCaseCount,
    int DuplicatesRemoved,
    int RawCaseCount,
    LivePilotSummary? LivePilot = null);

/// <summary>Compact live-pilot read summary. No personnel evaluation.</summary>
public sealed record LivePilotSummary(
    string TechnicianResourceId,
    string TechnicianName,
    DateOnly DateFrom,
    DateOnly DateTo,
    int PlenionPerformancesRead,
    int LinkedPerformances,
    int Exceptions,
    int SmallDeviations,
    int MatchUncertainty,
    int DataQuality,
    int Completed,
    int ProposedMatches,
    string Banner);

/// <summary>Local timing for human review sessions (append/update, no HR conclusions).</summary>
public sealed class AdminReviewSessionMetric
{
    public long Id { get; set; }
    public long PerformanceId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public string? Decision { get; set; }
    public string? MatcherStatus { get; set; }
    public bool? ProposedCandidateConfirmed { get; set; }
}

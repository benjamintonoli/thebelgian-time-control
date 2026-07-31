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

    public const string BannerTitle = "MatcherUsageMode = HumanReviewRequired";

    public const string BannerBody =
        "Matcherresultaten zijn voorstellen voor menselijke review. " +
        "Finale holdoutbeslissing: NO-GO voor automatische acceptatie. " +
        "Adminbevestiging is verplicht. Dit is een read-only pilot zonder Plenion-writeback.";
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
    PatternRelevant = 1,
    IndividualException = 2,
    High = 3,
}

/// <summary>
/// Append-only admin decision audit row. Never overwrites matcher source outcomes.
/// </summary>
public sealed class AdminReviewDecisionAudit
{
    public long Id { get; set; }
    public long PerformanceId { get; set; }
    public required string OriginalMatcherDecision { get; set; }
    public string? ProposedVisitCandidateId { get; set; }
    public string? ProposedVisitSourceStopIdsJson { get; set; }
    public required string AdminDecision { get; set; }
    public string? ChosenVisitCandidateId { get; set; }
    public string? ChosenVisitSourceStopIdsJson { get; set; }
    public string? Comment { get; set; }
    public required string Reviewer { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public required string MatcherCommit { get; set; }
    public required string ConfigurationHashSha256 { get; set; }
}

public sealed record AdminReviewFilter(
    string? Technician = null,
    DateOnly? FromDate = null,
    DateOnly? ThroughDate = null,
    AdminReviewStatus? ReviewStatus = null,
    string? MatcherStatus = null,
    int? MinimumDeviationMinutes = null,
    bool HighPriorityOnly = false,
    bool ProposedMatchesOnly = false,
    bool AmbiguousOrUnresolvedOnly = false);

public sealed record AdminReviewVisitSummary(
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

public sealed record AdminReviewCase(
    long PerformanceId,
    DateOnly Date,
    string Technician,
    DateTimeOffset PerformanceStart,
    DateTimeOffset PerformanceEnd,
    string PlenionAddress,
    string? Lacleunik,
    string? ProjectOrBonContext,
    string? PreviousPerformance,
    string? NextPerformance,
    string MatcherStatus,
    string MatchReason,
    bool MatcherProposedAcceptance,
    AdminReviewVisitSummary? ProposedVisit,
    IReadOnlyList<AdminReviewVisitSummary> CandidateVisits,
    GeocodeQualityClass GeocodeQuality,
    int MaxDeviationMinutes,
    SpotcheckPriorityTier Priority,
    bool RecurringSmallAdvantage,
    AdminReviewStatus ReviewStatus,
    string? LatestReviewer,
    string? LatestComment,
    string MatcherCommit,
    string ConfigurationHashSha256);

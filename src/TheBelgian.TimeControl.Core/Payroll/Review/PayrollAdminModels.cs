using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Admin-facing business decision grouping over one or more technical ReviewCases.
/// Does not replace PayrollFindingRecords or PayrollReviewCase.
/// </summary>
public sealed record PayrollAdminCase(
    string AdminCaseKey,
    PayrollReviewCategory Category,
    string ResourceId,
    string? DisplayName,
    DateOnly Date,
    PayrollFindingSeverity Severity,
    PayrollFindingStatus WorkflowStatus,
    string BusinessQuestion,
    string IssueSummary,
    string? KeyFact,
    string? BonNr,
    string? ProjectId,
    string? BookedSummary,
    string? PlannedSummary,
    string? DifferenceSummary,
    string? EvidenceSummary,
    string? HybridScenarioNote,
    string? FriendlyState,
    string? RuleHint,
    string ActionabilityHint,
    int UnderlyingReviewCaseCount,
    int UnderlyingPerformanceCount,
    decimal? TotalBookedHours,
    IReadOnlyList<PayrollReviewCase> UnderlyingCases,
    IReadOnlyList<int> FindingIds,
    IReadOnlyList<string> FindingKeys,
    string? DecisionCode,
    string? DecisionLabel,
    string? ReviewComment,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    bool AllowsBulkDisposition,
    IReadOnlyList<PayrollGuidedChoice> Choices,
    IReadOnlyList<PayrollAdminPerformanceDetail> Performances,
    string? TimeIntervalSummary = null,
    string? PerformanceDescriptionSummary = null,
    IReadOnlyList<string>? EvidenceChips = null);

/// <summary>
/// One underlying prestatie inside an AdminCase for dense triage UI.
/// </summary>
public sealed record PayrollAdminPerformanceDetail(
    string CaseKey,
    long? PerformanceId,
    string? TimeInterval,
    decimal? BookedHours,
    string? BonNr,
    string? ProjectId,
    string? PerformanceDescription,
    string? PerformanceMemo,
    string? IssueText,
    string? PlanningStatus,
    bool DescriptionPresent,
    bool PlanningPresent,
    string? FindingEvidence,
    IReadOnlyList<string> EvidenceChips);

public sealed record PayrollGuidedChoice(
    string DecisionCode,
    string Label,
    PayrollFindingStatus ResultStatus,
    bool RequiresComment);

public sealed record PayrollAdminQueueSummary(
    int ReviewCasesTotal,
    int AdminCasesTotal,
    int Open,
    int NeedsFollowUp,
    int Completed,
    int Dismissed,
    int HighOpen,
    IReadOnlyDictionary<PayrollReviewCategory, int> ReviewCasesByCategory,
    IReadOnlyDictionary<PayrollReviewCategory, int> AdminCasesByCategory,
    IReadOnlyDictionary<PayrollReviewCategory, int> UnresolvedAdminByCategory,
    IReadOnlyDictionary<PayrollReviewCategory, int> FollowUpAdminByCategory,
    IReadOnlyDictionary<PayrollReviewCategory, int> CompletedAdminByCategory,
    decimal OpenBookedHours = 0m,
    IReadOnlyDictionary<PayrollReviewCategory, decimal>? OpenBookedHoursByCategory = null);

public sealed record PayrollAdminQueuePage(
    int Year,
    int Month,
    PayrollReviewQueueSummary ReviewSummary,
    PayrollAdminQueueSummary AdminSummary,
    IReadOnlyList<PayrollAdminCase> AdminCases,
    IReadOnlyList<PayrollReviewCase> ReviewCases,
    IReadOnlyList<PayrollShadowEmployeeResult> EmployeesAlphabetical);

public sealed record PayrollAdminDecisionResult(
    string AdminCaseKey,
    string DecisionCode,
    string DecisionLabel,
    PayrollFindingStatus Status,
    int FindingsUpdated);

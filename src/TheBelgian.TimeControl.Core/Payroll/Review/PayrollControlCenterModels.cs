using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public sealed record PayrollControlCenterPage(
    int Year,
    int Month,
    PayrollShadowMonthStatus Status,
    DateTimeOffset? FinalizedAtUtc,
    string? FinalizedBy,
    bool IsReadOnly,
    PayrollControlMonthSummary Summary,
    IReadOnlyList<PayrollControlCard> ControlCards,
    IReadOnlyList<PayrollControlPriorityItem> PriorityQueue,
    IReadOnlyList<PayrollControlEmployeeRow> Employees,
    PayrollControlActionOverview Actions,
    PayrollControlImpactOverview Impact,
    PayrollMonthFinalizationBlockers Finalization,
    IReadOnlyList<PayrollShadowMonthSummary> AvailableMonths,
    PayrollControlDiagnostics Diagnostics,
    bool CreatePerformanceEnabled);

public sealed record PayrollControlMonthSummary(
    int IncludedEmployees,
    int ReadyEmployees,
    int AttentionEmployees,
    int OpenAdminCases,
    int FollowUpAdminCases,
    int CompletedAdminCases,
    int AdminCasesTotal,
    int ReviewCasesTotal,
    int PendingActions,
    int AppliedActions,
    int FinalizationBlockerCount,
    int ProgressCompleted,
    int ProgressTotal,
    int ProgressPercent);

public sealed record PayrollControlCard(
    PayrollReviewCategory Category,
    string Title,
    string NavigationPage,
    int Open,
    int FollowUp,
    int Completed,
    int Total,
    int ProgressCompleted,
    string? ExtraStatusLabel,
    int ExtraStatusCount);

public sealed record PayrollControlPriorityItem(
    string AdminCaseKey,
    string ResourceId,
    string? DisplayName,
    DateOnly Date,
    PayrollReviewCategory Category,
    string IssueSummary,
    string? ImpactSummary,
    PayrollFindingStatus Status,
    int PriorityScore,
    string NavigationPage,
    string? FocusKey,
    bool CreateRequiredButGated);

public sealed record PayrollControlEmployeeRow(
    string ResourceId,
    string DisplayName,
    decimal? ActualHours,
    decimal? TheoreticalHours,
    decimal? DifferenceHours,
    decimal? Overtime150Units,
    decimal? StandbyHours,
    decimal? KmAmount,
    decimal? CityAllowanceAmount,
    int OpenCases,
    int FollowUpCases,
    int PendingActions,
    bool IsReady);

public sealed record PayrollControlActionOverview(
    int Proposed,
    int ReadyForApproval,
    int Executing,
    int Applied,
    int Failed,
    int Stale,
    int Cancelled,
    int Blocked,
    int CreateRequiredButGated,
    IReadOnlyList<PayrollControlActionRow> HighlightRows);

public sealed record PayrollControlActionRow(
    Guid ActionId,
    string ResourceId,
    PayrollProposedActionType ActionType,
    PayrollProposedActionStatus Status,
    string? BlockReason,
    DateTimeOffset CreatedAtUtc);

public sealed record PayrollControlImpactOverview(
    decimal CurrentOvertime150Units,
    decimal CurrentStandbyHours,
    decimal CurrentCityAllowanceAmount,
    decimal CurrentKmAmount,
    decimal OpenBookedHours,
    decimal PendingDeleteHours,
    decimal PendingAdjustHours,
    decimal PotentialMissingTechHours,
    string CurrentCalculatedLabel,
    string PotentialIfAppliedLabel);

public sealed record PayrollControlDiagnostics(
    int RawFindingCount,
    int ReviewCaseCount,
    int AdminCaseCount,
    string ReconciliationNote);

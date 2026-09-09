using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Pure Control Center assembly over existing month/admin/finalization/action state.
/// No GPS, no Plenion rebuild, no second finalization engine.
/// </summary>
public static class PayrollControlCenterBuilder
{
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    private static readonly PayrollReviewCategory[] ControlOrder =
    [
        PayrollReviewCategory.Project300,
        PayrollReviewCategory.Project200,
        PayrollReviewCategory.Project100,
        PayrollReviewCategory.Standby,
        PayrollReviewCategory.MissingPerformance,
        PayrollReviewCategory.Overlap,
        PayrollReviewCategory.Other,
    ];

    public static PayrollControlCenterPage Build(
        PayrollShadowMonth month,
        PayrollAdminQueuePage adminQueue,
        PayrollMonthFinalizationBlockers finalization,
        IReadOnlyList<PayrollProposedActionRecord> actions,
        IReadOnlyList<PayrollShadowMonthSummary> availableMonths,
        bool createPerformanceEnabled,
        int? rawFindingCount = null)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(adminQueue);
        ArgumentNullException.ThrowIfNull(finalization);
        actions ??= [];
        availableMonths ??= [];

        var isFinalized = month.Status == PayrollShadowMonthStatus.Finalized;
        var adminCases = adminQueue.AdminCases;
        var adminSummary = adminQueue.AdminSummary;
        var included = adminQueue.EmployeesAlphabetical
            .Where(item => item.EligibilityStatus == PayrollEligibilityStatus.Included)
            .OrderBy(item => item.DisplayNameSnapshot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToArray();

        var openByResource = CountByResource(adminCases, PayrollFindingStatus.Open);
        var followByResource = CountByResource(adminCases, PayrollFindingStatus.NeedsFollowUp);
        var pendingActionsByResource = actions
            .Where(IsNonTerminalAction)
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var employees = included
            .Select(item =>
            {
                var open = openByResource.GetValueOrDefault(item.ResourceId);
                var follow = followByResource.GetValueOrDefault(item.ResourceId);
                var pending = pendingActionsByResource.GetValueOrDefault(item.ResourceId);
                var employeeFollowUp = item.ReviewStatus == PayrollEmployeeReviewStatus.NeedsFollowUp;
                var ready = open == 0
                    && follow == 0
                    && pending == 0
                    && !employeeFollowUp;
                return new PayrollControlEmployeeRow(
                    item.ResourceId,
                    item.DisplayNameSnapshot,
                    item.LegacyActualOrdinaryHours,
                    item.LegacyTheoreticalHours,
                    item.LegacyDifferenceHours,
                    item.Code135At150Units,
                    item.StandbyRoundedHours,
                    item.KmAmount,
                    item.CityAllowanceAmount,
                    open,
                    follow,
                    pending,
                    ready);
            })
            .ToArray();

        var readyCount = employees.Count(item => item.IsReady);
        var attentionCount = employees.Count(item => !item.IsReady);
        var openAdmin = adminSummary.Open;
        var followAdmin = adminSummary.NeedsFollowUp;
        var completedAdmin = adminSummary.Completed + adminSummary.Dismissed;
        var progressTotal = openAdmin + followAdmin + completedAdmin;
        var progressCompleted = completedAdmin;
        var progressPercent = progressTotal == 0
            ? 100
            : (int)Math.Round(100m * progressCompleted / progressTotal, MidpointRounding.AwayFromZero);

        var pendingActions = actions.Count(IsNonTerminalAction);
        var appliedActions = actions.Count(item => item.Status == PayrollProposedActionStatus.Applied);

        var summary = new PayrollControlMonthSummary(
            IncludedEmployees: included.Length,
            ReadyEmployees: readyCount,
            AttentionEmployees: attentionCount,
            OpenAdminCases: openAdmin,
            FollowUpAdminCases: followAdmin,
            CompletedAdminCases: completedAdmin,
            AdminCasesTotal: adminSummary.AdminCasesTotal,
            ReviewCasesTotal: adminSummary.ReviewCasesTotal,
            PendingActions: pendingActions,
            AppliedActions: appliedActions,
            FinalizationBlockerCount: finalization.Blockers.Count,
            ProgressCompleted: progressCompleted,
            ProgressTotal: progressTotal,
            ProgressPercent: progressPercent);

        var cards = BuildCards(adminSummary, adminCases);
        var createGated = CountCreateRequiredButGated(adminCases, createPerformanceEnabled);
        var actionOverview = BuildActionOverview(actions, createGated);
        var impact = BuildImpact(finalization, adminSummary, actions, adminCases, createPerformanceEnabled);
        var priority = BuildPriorityQueue(adminCases, finalization, actions, createPerformanceEnabled);
        var findings = rawFindingCount
            ?? adminCases.SelectMany(item => item.FindingIds).Distinct().Count();
        var diagnostics = new PayrollControlDiagnostics(
            findings,
            adminSummary.ReviewCasesTotal,
            adminSummary.AdminCasesTotal,
            $"Admin cases zijn de primaire werkvoorraad ({adminSummary.AdminCasesTotal}). "
            + $"Daaronder liggen {adminSummary.ReviewCasesTotal} review cases / {findings} findings. "
            + "Meerdere findings kunnen tot één admin case groeperen.");

        return new PayrollControlCenterPage(
            month.Year,
            month.Month,
            month.Status,
            month.FinalizedAtUtc,
            month.FinalizedBy,
            isFinalized,
            summary,
            cards,
            priority,
            employees,
            actionOverview,
            impact,
            finalization,
            availableMonths
                .OrderByDescending(item => item.Year)
                .ThenByDescending(item => item.Month)
                .ToArray(),
            diagnostics,
            createPerformanceEnabled);
    }

    public static string NavigationPageFor(PayrollReviewCategory category) => category switch
    {
        PayrollReviewCategory.Project300 => "./Workbench",
        PayrollReviewCategory.Project200 => "./Project200Workbench",
        PayrollReviewCategory.Project100 => "./Project100Workbench",
        PayrollReviewCategory.Standby => "./StandbyWorkbench",
        PayrollReviewCategory.Overlap => "./OverlapWorkbench",
        PayrollReviewCategory.MissingPerformance => "./MissingTechnicianWorkbench",
        _ => "./Queue",
    };

    public static int PriorityScore(
        PayrollAdminCase adminCase,
        bool isFinalizationRelevant,
        bool hasPendingDestructiveAction)
    {
        var score = 0;
        if (isFinalizationRelevant)
        {
            score += 10_000;
        }

        if (adminCase.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp)
        {
            score += 5_000;
        }
        else if (adminCase.WorkflowStatus == PayrollFindingStatus.Open)
        {
            score += 4_000;
        }

        if (adminCase.Severity == PayrollFindingSeverity.High)
        {
            score += 2_000;
        }

        if (string.Equals(adminCase.FriendlyState, "Sterk bewijs", StringComparison.Ordinal))
        {
            score += 1_500;
        }

        if (adminCase.Category == PayrollReviewCategory.Overlap
            && adminCase.Severity == PayrollFindingSeverity.High)
        {
            score += 1_800; // exact duplicates / GPS-supported corrections
        }

        if (adminCase.Category == PayrollReviewCategory.MissingPerformance
            && adminCase.Severity == PayrollFindingSeverity.High)
        {
            score += 1_400;
        }

        if (hasPendingDestructiveAction)
        {
            score += 1_200;
        }

        score += CategoryWeight(adminCase.Category);
        var hours = adminCase.TotalBookedHours ?? 0m;
        score += (int)Math.Min(500, Math.Round(hours * 20m, MidpointRounding.AwayFromZero));
        return score;
    }

    private static List<PayrollControlCard> BuildCards(
        PayrollAdminQueueSummary summary,
        IReadOnlyList<PayrollAdminCase> adminCases)
    {
        var cards = new List<PayrollControlCard>(ControlOrder.Length);
        foreach (var category in ControlOrder)
        {
            var open = summary.UnresolvedAdminByCategory.GetValueOrDefault(category);
            var follow = summary.FollowUpAdminByCategory.GetValueOrDefault(category);
            var completed = summary.CompletedAdminByCategory.GetValueOrDefault(category);
            var total = open + follow + completed;
            var (extraLabel, extraCount) = ExtraStatusFor(category, adminCases);
            cards.Add(new PayrollControlCard(
                category,
                CardTitle(category),
                NavigationPageFor(category),
                open,
                follow,
                completed,
                total,
                completed,
                ExtraStatusLabel: extraLabel,
                ExtraStatusCount: extraCount));
        }

        return cards;
    }

    private static (string? Label, int Count) ExtraStatusFor(
        PayrollReviewCategory category,
        IReadOnlyList<PayrollAdminCase> adminCases)
    {
        var openOrFollow = adminCases
            .Where(item => item.Category == category
                && item.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp)
            .ToList();
        if (openOrFollow.Count == 0)
        {
            return (null, 0);
        }

        if (category == PayrollReviewCategory.Overlap)
        {
            var high = openOrFollow.Count(item => item.Severity == PayrollFindingSeverity.High);
            var review = openOrFollow.Count(item => item.Severity != PayrollFindingSeverity.High);
            return ($"High {high} / Review {review}", high);
        }

        if (category == PayrollReviewCategory.MissingPerformance)
        {
            var high = openOrFollow.Count(item => item.Severity == PayrollFindingSeverity.High);
            var review = openOrFollow.Count(item => item.Severity == PayrollFindingSeverity.Review);
            return ($"High {high} / Review {review}", high);
        }

        return (null, 0);
    }

    private static PayrollControlPriorityItem[] BuildPriorityQueue(
        IReadOnlyList<PayrollAdminCase> adminCases,
        PayrollMonthFinalizationBlockers finalization,
        IReadOnlyList<PayrollProposedActionRecord> actions,
        bool createPerformanceEnabled)
    {
        var destructiveByResource = actions
            .Where(item =>
                item.ActionType == PayrollProposedActionType.DeleteExistingPerformance
                && item.Status is PayrollProposedActionStatus.ReadyForApproval
                    or PayrollProposedActionStatus.Failed
                    or PayrollProposedActionStatus.Stale
                    or PayrollProposedActionStatus.Blocked)
            .Select(item => item.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        var unresolved = adminCases
            .Where(item => item.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp)
            .Select(item =>
            {
                var createGated = IsCreateRequiredButGated(item, createPerformanceEnabled);
                var score = PriorityScore(
                    item,
                    isFinalizationRelevant: finalization.OpenReviewCases > 0 || finalization.FollowUpReviewCases > 0,
                    hasPendingDestructiveAction: destructiveByResource.Contains(item.ResourceId));
                var impact = item.TotalBookedHours is > 0
                    ? item.TotalBookedHours.Value.ToString("0.##", Belgian) + " u"
                    : null;
                return new PayrollControlPriorityItem(
                    item.AdminCaseKey,
                    item.ResourceId,
                    item.DisplayName,
                    item.Date,
                    item.Category,
                    item.IssueSummary,
                    impact,
                    item.WorkflowStatus,
                    score,
                    NavigationPageFor(item.Category),
                    item.AdminCaseKey,
                    createGated);
            })
            .OrderByDescending(item => item.PriorityScore)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal)
            .Take(25)
            .ToArray();
        return unresolved;
    }

    private static PayrollControlActionOverview BuildActionOverview(
        IReadOnlyList<PayrollProposedActionRecord> actions,
        int createGated)
    {
        var highlight = actions
            .Where(item => item.Status is PayrollProposedActionStatus.Failed
                or PayrollProposedActionStatus.Stale
                or PayrollProposedActionStatus.Blocked
                or PayrollProposedActionStatus.ReadyForApproval)
            .OrderBy(item => StatusRank(item.Status))
            .ThenByDescending(item => item.CreatedAtUtc)
            .Take(30)
            .Select(item => new PayrollControlActionRow(
                item.ActionId,
                item.ResourceId,
                item.ActionType,
                item.Status,
                item.BlockReason,
                item.CreatedAtUtc))
            .ToArray();

        return new PayrollControlActionOverview(
            Proposed: actions.Count(item => item.Status == PayrollProposedActionStatus.Proposed),
            ReadyForApproval: actions.Count(item => item.Status == PayrollProposedActionStatus.ReadyForApproval),
            Executing: actions.Count(item => item.Status == PayrollProposedActionStatus.Executing),
            Applied: actions.Count(item => item.Status == PayrollProposedActionStatus.Applied),
            Failed: actions.Count(item => item.Status == PayrollProposedActionStatus.Failed),
            Stale: actions.Count(item => item.Status == PayrollProposedActionStatus.Stale),
            Cancelled: actions.Count(item => item.Status == PayrollProposedActionStatus.Cancelled),
            Blocked: actions.Count(item => item.Status == PayrollProposedActionStatus.Blocked),
            CreateRequiredButGated: createGated,
            HighlightRows: highlight);
    }

    private static PayrollControlImpactOverview BuildImpact(
        PayrollMonthFinalizationBlockers finalization,
        PayrollAdminQueueSummary adminSummary,
        IReadOnlyList<PayrollProposedActionRecord> actions,
        IReadOnlyList<PayrollAdminCase> adminCases,
        bool createPerformanceEnabled)
    {
        var pendingDelete = 0m;
        var pendingAdjust = 0m;
        foreach (var action in actions.Where(item => item.Status == PayrollProposedActionStatus.ReadyForApproval))
        {
            var hours = TryReadProposalHours(action.ProposalSnapshotJson);
            if (hours is null)
            {
                continue;
            }

            if (action.ActionType == PayrollProposedActionType.DeleteExistingPerformance)
            {
                pendingDelete += hours.Value;
            }
            else if (action.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime)
            {
                pendingAdjust += hours.Value;
            }
        }

        var potentialMissing = createPerformanceEnabled
            ? 0m
            : adminCases
                .Where(item =>
                    item.Category == PayrollReviewCategory.MissingPerformance
                    && item.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp
                    && string.Equals(item.FriendlyState, "Sterk bewijs", StringComparison.Ordinal))
                .Sum(item => item.TotalBookedHours ?? 0m);

        return new PayrollControlImpactOverview(
            finalization.FinancialSummary.TotalOvertime150Units,
            finalization.FinancialSummary.TotalStandby200Hours,
            finalization.FinancialSummary.TotalCityAllowanceAmount,
            finalization.FinancialSummary.TotalKmAmount,
            adminSummary.OpenBookedHours,
            pendingDelete,
            pendingAdjust,
            potentialMissing,
            "Huidige berekende snapshot (canonical calculator)",
            "Potentieel bij toegepaste voorstellen (alleen betrouwbare uren)");
    }

    private static Dictionary<string, int> CountByResource(
        IReadOnlyList<PayrollAdminCase> adminCases,
        PayrollFindingStatus status) =>
        adminCases
            .Where(item => item.WorkflowStatus == status)
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static bool IsNonTerminalAction(PayrollProposedActionRecord action) =>
        action.Status is PayrollProposedActionStatus.Proposed
            or PayrollProposedActionStatus.ReadyForApproval
            or PayrollProposedActionStatus.Executing
            or PayrollProposedActionStatus.Blocked
            or PayrollProposedActionStatus.Failed
            or PayrollProposedActionStatus.Stale;

    private static bool IsCreateRequiredButGated(PayrollAdminCase adminCase, bool createEnabled) =>
        !createEnabled
        && adminCase.Category == PayrollReviewCategory.MissingPerformance
        && adminCase.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp
        && (string.Equals(adminCase.FriendlyState, "Sterk bewijs", StringComparison.Ordinal)
            || string.Equals(adminCase.DecisionCode, PayrollGuidedDecisionCodes.MissingTechConfirmed, StringComparison.Ordinal));

    private static int CountCreateRequiredButGated(
        IReadOnlyList<PayrollAdminCase> adminCases,
        bool createEnabled) =>
        adminCases.Count(item => IsCreateRequiredButGated(item, createEnabled));

    private static int CategoryWeight(PayrollReviewCategory category) => category switch
    {
        PayrollReviewCategory.Overlap => 200,
        PayrollReviewCategory.MissingPerformance => 160,
        PayrollReviewCategory.Standby => 100,
        PayrollReviewCategory.Project300 => 90,
        PayrollReviewCategory.Project200 => 70,
        PayrollReviewCategory.Project100 => 60,
        _ => 10,
    };

    private static int StatusRank(PayrollProposedActionStatus status) => status switch
    {
        PayrollProposedActionStatus.Failed => 0,
        PayrollProposedActionStatus.Stale => 1,
        PayrollProposedActionStatus.Blocked => 2,
        PayrollProposedActionStatus.ReadyForApproval => 3,
        _ => 9,
    };

    private static string CardTitle(PayrollReviewCategory category) => category switch
    {
        PayrollReviewCategory.Project300 => "PROJECT 300",
        PayrollReviewCategory.Project200 => "PROJECT 200",
        PayrollReviewCategory.Project100 => "OPLEIDING / PROJECT 100",
        PayrollReviewCategory.Standby => "WACHTDIENST",
        PayrollReviewCategory.MissingPerformance => "ONTBREKENDE PRESTATIES",
        PayrollReviewCategory.Overlap => "DUBBELE UREN",
        _ => "OTHER",
    };

    private static decimal? TryReadProposalHours(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var name in new[] { "AtlHours", "CurrentAtlHours", "BookedHours", "Hours" })
            {
                if (root.TryGetProperty(name, out var prop) && prop.TryGetDecimal(out var value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}

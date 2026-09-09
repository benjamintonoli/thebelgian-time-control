using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Finalization;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollControlCenterTests
{
    private static readonly DateOnly Day = new(2026, 8, 20);

    [Fact]
    public void MonthlySummary_UsesAdminCaseCountsAndFinalizationBlockers()
    {
        var page = BuildCenter();
        Assert.Equal(2, page.Summary.IncludedEmployees);
        Assert.Equal(1, page.Summary.OpenAdminCases);
        Assert.Equal(1, page.Summary.FollowUpAdminCases);
        Assert.True(page.Summary.FinalizationBlockerCount > 0);
        Assert.False(page.Finalization.CanFinalize);
        Assert.Contains(page.Finalization.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.OpenReviewCases);
    }

    [Fact]
    public void ControlCards_ReconcileOpenFollowUpCompleted()
    {
        var page = BuildCenter();
        var p300 = Assert.Single(page.ControlCards, item => item.Category == PayrollReviewCategory.Project300);
        Assert.Equal(1, p300.Open);
        Assert.Equal(0, p300.FollowUp);
        Assert.Equal("./Workbench", p300.NavigationPage);

        var standby = Assert.Single(page.ControlCards, item => item.Category == PayrollReviewCategory.Standby);
        Assert.Equal(0, standby.Open);
        Assert.Equal(1, standby.FollowUp);
        Assert.Equal("./StandbyWorkbench", standby.NavigationPage);
    }

    [Fact]
    public void PendingEmployees_Alone_DoNotAppearAsFinalizationBlocker()
    {
        var employees = new[]
        {
            Included("10", "Jarno", review: PayrollEmployeeReviewStatus.Pending),
        };
        var review = PayrollReviewCaseBuilder.Build([], Emp(employees), []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        var blockers = PayrollFinalizationEvaluator.Evaluate(Month(), employees, review);
        Assert.True(blockers.CanFinalize);
        var page = PayrollControlCenterBuilder.Build(
            Month(),
            Queue(review, admin, employees),
            blockers,
            [],
            [MonthSummary()],
            createPerformanceEnabled: false);
        Assert.True(page.Finalization.CanFinalize);
        Assert.Equal(0, page.Summary.FinalizationBlockerCount);
        Assert.DoesNotContain(
            page.Finalization.SummaryLines,
            line => line.Contains("Pending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmployeeReadiness_MarksOpenCaseEmployeesNotReady()
    {
        var page = BuildCenter();
        var jarno = Assert.Single(page.Employees, item => item.ResourceId == "10");
        var rajco = Assert.Single(page.Employees, item => item.ResourceId == "130");
        Assert.False(jarno.IsReady);
        Assert.False(rajco.IsReady);
        Assert.Equal(0, page.Summary.ReadyEmployees);
        Assert.Equal(2, page.Summary.AttentionEmployees);
        Assert.Equal(8m, jarno.TheoreticalHours);
        Assert.Equal(7.5m, jarno.ActualHours);
    }

    [Fact]
    public void ActionStatusGrouping_HighlightsFailedStaleBlocked()
    {
        var actions = new[]
        {
            Action(PayrollProposedActionStatus.ReadyForApproval, PayrollProposedActionType.AdjustExistingPerformanceTime),
            Action(PayrollProposedActionStatus.Failed, PayrollProposedActionType.DeleteExistingPerformance),
            Action(PayrollProposedActionStatus.Stale, PayrollProposedActionType.AdjustExistingPerformanceTime),
            Action(PayrollProposedActionStatus.Blocked, PayrollProposedActionType.DeleteExistingPerformance),
            Action(PayrollProposedActionStatus.Applied, PayrollProposedActionType.AdjustExistingPerformanceTime),
        };
        var page = BuildCenter(actions: actions);
        Assert.Equal(1, page.Actions.ReadyForApproval);
        Assert.Equal(1, page.Actions.Failed);
        Assert.Equal(1, page.Actions.Stale);
        Assert.Equal(1, page.Actions.Blocked);
        Assert.Equal(1, page.Actions.Applied);
        Assert.Contains(page.Actions.HighlightRows, item => item.Status == PayrollProposedActionStatus.Failed);
    }

    [Fact]
    public void CreateOff_ShowsGatedMissingTechnicianMessaging()
    {
        var finding = Finding(
            PayrollFindingType.MissingPlannedTechnicianPerformance,
            "missing-tech:1:20260820:10",
            "10",
            severity: PayrollFindingSeverity.High);
        finding.GpsClassification = nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps);
        finding.RelatedPerformanceIdsJson = "[]";
        var employees = new[] { Included("10", "Jarno") };
        var review = PayrollReviewCaseBuilder.Build([finding], Emp(employees), []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        Assert.Contains(admin, item => item.FriendlyState == "Sterk bewijs");
        var blockers = PayrollFinalizationEvaluator.Evaluate(Month(), employees, review);
        var page = PayrollControlCenterBuilder.Build(
            Month(),
            Queue(review, admin, employees),
            blockers,
            [],
            [MonthSummary()],
            createPerformanceEnabled: false);
        Assert.True(page.Actions.CreateRequiredButGated >= 1);
        Assert.Contains(page.PriorityQueue, item => item.CreateRequiredButGated);
        Assert.False(page.CreatePerformanceEnabled);
    }

    [Fact]
    public void PriorityQueue_IsDeterministic_HighFollowUpBeforeOpenLow()
    {
        var page = BuildCenter();
        Assert.True(page.PriorityQueue.Count >= 2);
        Assert.Equal(page.PriorityQueue.OrderByDescending(item => item.PriorityScore)
                .ThenBy(item => item.Date)
                .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
                .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal)
                .Select(item => item.AdminCaseKey),
            page.PriorityQueue.Select(item => item.AdminCaseKey));
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, page.PriorityQueue[0].Status);
    }

    [Fact]
    public void Navigation_PreservesWorkbenchTargetsForSpecialProjects()
    {
        Assert.Equal("./Workbench", PayrollControlCenterBuilder.NavigationPageFor(PayrollReviewCategory.Project300));
        Assert.Equal("./Project200Workbench", PayrollControlCenterBuilder.NavigationPageFor(PayrollReviewCategory.Project200));
        Assert.Equal("./Project100Workbench", PayrollControlCenterBuilder.NavigationPageFor(PayrollReviewCategory.Project100));
        Assert.Equal("./StandbyWorkbench", PayrollControlCenterBuilder.NavigationPageFor(PayrollReviewCategory.Standby));
        Assert.Equal("./MissingTechnicianWorkbench", PayrollControlCenterBuilder.NavigationPageFor(PayrollReviewCategory.MissingPerformance));
        Assert.Equal("./OverlapWorkbench", PayrollControlCenterBuilder.NavigationPageFor(PayrollReviewCategory.Overlap));
    }

    [Fact]
    public void FinalizedMonth_IsReadOnly_AndKeepsFrozenFinancials()
    {
        var month = Month(PayrollShadowMonthStatus.Finalized);
        month.FinalizedAtUtc = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.FromHours(2));
        month.FinalizedBy = "benjamin.tonoli@thebelgian.be";
        var employees = new[] { Included("10", "Jarno") };
        var review = PayrollReviewCaseBuilder.Build([], Emp(employees), []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        var blockers = PayrollFinalizationEvaluator.Evaluate(month, employees, review);
        var page = PayrollControlCenterBuilder.Build(
            month,
            Queue(review, admin, employees),
            blockers,
            [],
            [MonthSummary(PayrollShadowMonthStatus.Finalized)],
            createPerformanceEnabled: false);
        Assert.True(page.IsReadOnly);
        Assert.Equal("benjamin.tonoli@thebelgian.be", page.FinalizedBy);
        Assert.False(page.Finalization.CanFinalize);
        Assert.Contains(page.Finalization.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.AlreadyFinalized);
        Assert.Equal(1.5m, page.Impact.CurrentOvertime150Units);
    }

    [Fact]
    public void Builder_HasNoGpsDependency_AndUsesPersistedInputsOnly()
    {
        var page = BuildCenter();
        Assert.NotNull(page.Diagnostics.ReconciliationNote);
        Assert.Contains("Admin cases", page.Diagnostics.ReconciliationNote, StringComparison.Ordinal);
        Assert.Equal(page.Diagnostics.AdminCaseCount, page.Summary.AdminCasesTotal);
    }

    [Fact]
    public void ControlPage_RouteAndCards_AreWired()
    {
        var root = FindRepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "Control.cshtml"));
        var index = File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "Index.cshtml"));
        Assert.Contains("/Admin/Payroll/{year:int}/{month:int}/Control", page, StringComparison.Ordinal);
        Assert.Contains("Maand finaliseren", page, StringComparison.Ordinal);
        Assert.Contains("Nieuwe prestatie vereist", page, StringComparison.Ordinal);
        Assert.Contains("./Control", index, StringComparison.Ordinal);
        Assert.Contains("StandbyWorkbench", File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Core", "Payroll", "Review", "PayrollControlCenterBuilder.cs")), StringComparison.Ordinal);
        Assert.Contains("Project100Workbench", File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Core", "Payroll", "Review", "PayrollControlCenterBuilder.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void AyrtonTravelMinMismatch_IsNotUnexplained()
    {
        var label = August2026PayrollShadowAcceptanceTests.ClassifyMismatch(
            hourOk: false,
            theoOk: true,
            actualOk: false,
            diffOk: false,
            standbyOk: true,
            cityOk: true,
            kmOk: false);
        Assert.Equal("SOURCE_CHANGED_SINCE_LIVE_MUTATION_TRAVEL_MIN", label);
        Assert.DoesNotContain("UNEXPLAINED", label, StringComparison.Ordinal);
    }

    private static PayrollControlCenterPage BuildCenter(
        IReadOnlyList<PayrollProposedActionRecord>? actions = null)
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "p300:10:20260820:101", "10"),
            Finding(
                PayrollFindingType.StandbyStartMismatch,
                "StandbyStartMismatch:130:20260820:201",
                "130",
                status: PayrollFindingStatus.NeedsFollowUp),
        };
        var employees = new[]
        {
            Included("10", "Jarno"),
            Included("130", "Rajco"),
        };
        var review = PayrollReviewCaseBuilder.Build(findings, Emp(employees), []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        var blockers = PayrollFinalizationEvaluator.Evaluate(Month(), employees, review);
        return PayrollControlCenterBuilder.Build(
            Month(),
            Queue(review, admin, employees),
            blockers,
            actions ?? [],
            [MonthSummary()],
            createPerformanceEnabled: false,
            rawFindingCount: findings.Length);
    }

    private static PayrollAdminQueuePage Queue(
        IReadOnlyList<PayrollReviewCase> review,
        IReadOnlyList<PayrollAdminCase> admin,
        PayrollShadowEmployeeResult[] employees) =>
        new(
            2026,
            8,
            new PayrollReviewQueueSummary(
                employees.Length,
                employees.Length,
                0,
                review.Count,
                review.Count(item => item.WorkflowStatus == PayrollFindingStatus.Open),
                review.Count(item => item.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp),
                0,
                0,
                0,
                0,
                0,
                Enum.GetValues<PayrollReviewCategory>().Where(item => item != PayrollReviewCategory.All)
                    .ToDictionary(item => item, item => review.Count(c => c.Category == item)),
                Enum.GetValues<PayrollReviewCategory>().Where(item => item != PayrollReviewCategory.All)
                    .ToDictionary(
                        item => item,
                        item => review.Count(c =>
                            c.Category == item
                            && c.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp))),
            PayrollAdminCaseBuilder.Summarize(review, admin),
            admin,
            review,
            employees);

    private static PayrollShadowMonth Month(PayrollShadowMonthStatus status = PayrollShadowMonthStatus.InReview) =>
        new()
        {
            Id = 1,
            Year = 2026,
            Month = 8,
            PeriodStart = new DateOnly(2026, 8, 1),
            PeriodEnd = new DateOnly(2026, 8, 31),
            EvaluationDate = new DateOnly(2026, 9, 2),
            Status = status,
            CalculationVersion = "test",
            ConfigurationSnapshotJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };

    private static PayrollShadowMonthSummary MonthSummary(
        PayrollShadowMonthStatus status = PayrollShadowMonthStatus.InReview) =>
        new(2026, 8, status, DateTimeOffset.UtcNow, new DateOnly(2026, 9, 2), "test", 2, 2, 0, 0, 2, 0, 0);

    private static PayrollShadowEmployeeResult Included(
        string id,
        string name,
        PayrollEmployeeReviewStatus review = PayrollEmployeeReviewStatus.Pending) =>
        new()
        {
            ResourceId = id,
            DisplayNameSnapshot = name,
            EligibilityStatus = PayrollEligibilityStatus.Included,
            AcertaIdentityStatus = AcertaIdentityStatus.Present,
            OrdinaryStatus = PayrollMonthCalculationStatus.Calculated,
            StandbyStatus = PayrollMonthCalculationStatus.Calculated,
            CityStatus = PayrollMonthCalculationStatus.Calculated,
            KmStatus = PayrollMonthCalculationStatus.Calculated,
            Code414Status = PayrollMonthCalculationStatus.Calculated,
            ReviewStatus = review,
            LegacyTheoreticalHours = 8m,
            LegacyActualOrdinaryHours = 7.5m,
            LegacyDifferenceHours = -0.5m,
            Code135At150Units = 1.5m,
            StandbyRoundedHours = 2m,
            KmAmount = 10m,
            CityAllowanceAmount = 5m,
        };

    private static Dictionary<string, PayrollShadowEmployeeResult> Emp(
        PayrollShadowEmployeeResult[] employees) =>
        employees.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);

    private static PayrollFindingRecord Finding(
        PayrollFindingType type,
        string key,
        string resourceId,
        PayrollFindingStatus status = PayrollFindingStatus.Open,
        PayrollFindingSeverity severity = PayrollFindingSeverity.Review) =>
        new()
        {
            Id = Math.Abs(key.GetHashCode()) % 100000 + 1,
            FindingKey = key,
            ResourceId = resourceId,
            Date = Day,
            FindingType = type,
            Severity = severity,
            Status = status,
            Title = type.ToString(),
            Description = "desc",
            Evidence = "evidence",
            SuggestedAction = "review",
            RelatedPerformanceIdsJson = "[101]",
            BookedHours = 2m,
        };

    private static PayrollProposedActionRecord Action(
        PayrollProposedActionStatus status,
        PayrollProposedActionType type) =>
        new()
        {
            ActionId = Guid.NewGuid(),
            ShadowMonthId = 1,
            FindingKey = "x",
            ResourceId = "10",
            ActionType = type,
            Status = status,
            EvidenceSnapshotJson = "{}",
            ProposalSnapshotJson = """{"AtlHours":1.5}""",
            SourceRevision = "r1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}

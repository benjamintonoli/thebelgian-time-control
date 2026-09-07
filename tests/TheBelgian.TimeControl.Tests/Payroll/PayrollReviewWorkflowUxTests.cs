using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollReviewWorkflowUxTests
{
    [Fact]
    public void PeriodConfig_StartingAfterAugust_IsNotActiveForAugust()
    {
        var config = new PayrollEmployeeConfiguration(
            "10",
            new DateOnly(2026, 9, 7),
            null,
            PayrollEligibilityStatus.Included,
            "RosterConfirmation",
            null,
            PayrollEligibilityDecisionSource.Admin);
        Assert.False(config.IsActiveFor(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));
        Assert.True(config.IsActiveFor(new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 7)));
    }

    [Fact]
    public void ReviewLabels_AreDutchFacing()
    {
        Assert.Equal("Te controleren", PayrollReviewLabels.ReviewStatus(PayrollEmployeeReviewStatus.Pending));
        Assert.Equal("Goedgekeurd", PayrollReviewLabels.ReviewStatus(PayrollEmployeeReviewStatus.Accepted));
        Assert.Equal("Opvolging nodig", PayrollReviewLabels.ReviewStatus(PayrollEmployeeReviewStatus.NeedsFollowUp));
        Assert.Equal("Review bezig", PayrollReviewLabels.MonthStatus(PayrollShadowMonthStatus.InReview));
    }

    [Fact]
    public void MonthMarkup_ShowsApplyRosterAndReviewProgress()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "Month.cshtml"));
        Assert.Contains("Payrolllijst toepassen op deze maand", markup);
        Assert.Contains("Review starten", markup);
        Assert.Contains("Review bezig", markup);
        Assert.Contains("Opvolging", markup);
        Assert.Contains("PayrollReviewLabels.ReviewStatus", markup);
        Assert.Contains("goedgekeurd", markup);
        Assert.Contains("Opvolging nodig", markup);
        Assert.Contains("Maand controleren en afsluiten", markup);
        Assert.Contains("./Finalize", markup);
        Assert.Contains("informatief", markup);
        Assert.Contains("|Diff| ≥ 8u", markup);
        Assert.Contains("disabled", markup);
        Assert.Contains("Medewerkers (alfabetisch)", markup);
        Assert.Contains("./Queue", markup);
    }

    [Fact]
    public async Task Month_StartReviewButton_OnlyWhenReadyForReview()
    {
        var service = new RecordingService
        {
            Detail = CreateDetail(PayrollShadowMonthStatus.InReview),
        };
        var page = CreateMonthPage(service);
        await page.OnGetAsync(default);
        Assert.Equal(PayrollShadowMonthStatus.InReview, page.Detail!.Month.Status);
        Assert.NotNull(page.FinalizationBlockers);
    }

    [Fact]
    public async Task Month_ApplyRoster_PostsThroughService()
    {
        var service = new RecordingService
        {
            Detail = CreateDetail(PayrollShadowMonthStatus.ReadyForReview),
            Insight = new PayrollMonthPeriodEligibilityInsight(
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                45,
                0,
                0,
                true,
                new DateOnly(2026, 9, 7),
                44,
                1,
                "De payrollselectie is geldig vanaf 07/09/2026 en is niet van toepassing op 08/2026."),
            ApplyResult = new ApplyConfirmedRosterToMonthResult(2026, 8, new DateOnly(2026, 8, 1), 44, 1, 0),
        };
        var page = CreateMonthPage(service);
        var result = await page.OnPostApplyRosterAsync(default);
        Assert.IsType<PageResult>(result);
        Assert.True(service.ApplyCalled);
        Assert.Contains("44 Included", page.Message);
        Assert.Contains("1 Excluded", page.Message);
    }

    private static MonthModel CreateMonthPage(RecordingService service) =>
        new(
            service,
            new StubReviewQueueService(),
            new FakeUserContext(),
            Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
            Options.Create(new AdminReviewWorkflowOptions { DefaultReviewer = "Ada Admin" }),
            NullLogger<MonthModel>.Instance)
        {
            Year = 2026,
            Month = 8,
        };

    private static PayrollShadowMonthDetail CreateDetail(PayrollShadowMonthStatus status)
    {
        var month = new PayrollShadowMonth
        {
            Id = 1,
            Year = 2026,
            Month = 8,
            PeriodStart = new DateOnly(2026, 8, 1),
            PeriodEnd = new DateOnly(2026, 8, 31),
            EvaluationDate = new DateOnly(2026, 9, 1),
            Status = status,
            CalculationVersion = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        var summary = new PayrollShadowMonthSummary(
            2026, 8, status, month.CreatedAtUtc, month.EvaluationDate, "test",
            45, 0, 0, 45, 45, 0, 0);
        return new PayrollShadowMonthDetail(month, summary, []);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "TheBelgian.TimeControl.Web")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public AuthenticatedActor? CurrentUser => new("Ada Admin", "sub", "Ada Admin");

        public AuthenticatedActor RequireActor(string developmentFallbackReviewer) =>
            new(developmentFallbackReviewer, "sub", developmentFallbackReviewer);
    }

    private sealed class StubReviewQueueService : IPayrollReviewQueueService
    {
        public Task<PayrollReviewQueuePage> GetQueueAsync(
            int year,
            int month,
            PayrollReviewQueueFilter filter,
            CancellationToken cancellationToken)
        {
            var emptyCategories = Enum.GetValues<PayrollReviewCategory>()
                .Where(item => item != PayrollReviewCategory.All)
                .ToDictionary(item => item, _ => 0);
            return Task.FromResult(new PayrollReviewQueuePage(
                year,
                month,
                new PayrollReviewQueueSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories),
                [],
                [],
                [],
                []));
        }

        public Task<PayrollAdminQueuePage> GetAdminQueueAsync(
            int year,
            int month,
            PayrollReviewQueueFilter filter,
            CancellationToken cancellationToken)
        {
            var emptyCategories = Enum.GetValues<PayrollReviewCategory>()
                .Where(item => item != PayrollReviewCategory.All)
                .ToDictionary(item => item, _ => 0);
            return Task.FromResult(new PayrollAdminQueuePage(
                year,
                month,
                new PayrollReviewQueueSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories),
                new PayrollAdminQueueSummary(0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories, emptyCategories, emptyCategories, emptyCategories),
                [],
                [],
                []));
        }

        public Task SetCaseStatusAsync(
            int year,
            int month,
            string caseKey,
            PayrollFindingStatus status,
            string? comment,
            string actor,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PayrollReviewBulkUpdateResult> BulkSetCaseStatusAsync(
            int year,
            int month,
            IReadOnlyList<string> caseKeys,
            PayrollFindingStatus status,
            string comment,
            string actor,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PayrollReviewBulkUpdateResult(caseKeys.Count, 0, []));

        public Task<PayrollAdminDecisionResult> SetAdminDecisionAsync(
            int year,
            int month,
            string adminCaseKey,
            string decisionCode,
            string? comment,
            string actor,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PayrollAdminDecisionResult(adminCaseKey, decisionCode, decisionCode, PayrollFindingStatus.Reviewed, 0));

        public Task<PayrollReviewBulkUpdateResult> BulkSetAdminDecisionAsync(
            int year,
            int month,
            IReadOnlyList<string> adminCaseKeys,
            string decisionCode,
            string comment,
            string actor,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PayrollReviewBulkUpdateResult(adminCaseKeys.Count, 0, []));
    }

    private sealed class RecordingService : IPayrollShadowService
    {
        public PayrollShadowMonthDetail? Detail { get; set; }
        public PayrollMonthPeriodEligibilityInsight? Insight { get; set; }
        public ApplyConfirmedRosterToMonthResult? ApplyResult { get; set; }
        public bool ApplyCalled { get; private set; }

        public Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(
            int year, int month, PayrollShadowEmployeeFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(Detail);

        public Task<PayrollMonthPeriodEligibilityInsight> GetPeriodEligibilityInsightAsync(
            int year, int month, CancellationToken cancellationToken) =>
            Task.FromResult(Insight ?? new PayrollMonthPeriodEligibilityInsight(
                new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 0, 0, 0, false, null, 0, 0, null));

        public Task<PayrollMonthFinalizationBlockers> GetFinalizationBlockersAsync(
            int year, int month, CancellationToken cancellationToken) =>
            Task.FromResult(new PayrollMonthFinalizationBlockers(
                false,
                PendingIncluded: 1,
                NeedsFollowUpIncluded: 0,
                NeedsDecision: 1,
                MissingAcertaIncluded: 0,
                IncludedCount: 0,
                ExcludedCount: 0,
                OpenReviewCases: 0,
                FollowUpReviewCases: 0,
                ReviewedReviewCases: 0,
                ResolvedReviewCases: 0,
                DismissedReviewCases: 0,
                ReviewCasesTotal: 0,
                IncompleteCalculationIncluded: 0,
                Blockers:
                [
                    new("ELIGIBILITY_NEEDS_DECISION", 1, "1 medewerker(s) met NeedsDecision."),
                ],
                FinancialSummary: new PayrollMonthFinancialSummary(0, 0, 0, 0, 0),
                CalculationVersion: "test",
                HasConfigurationSnapshot: true,
                SummaryLines: ["1 medewerker(s) met NeedsDecision."]));

        public Task<ApplyConfirmedRosterToMonthResult> ApplyConfirmedRosterToMonthAsync(
            int year, int month, string actor, string? comment, CancellationToken cancellationToken)
        {
            ApplyCalled = true;
            return Task.FromResult(ApplyResult!);
        }

        public Task AddManualPayrollEmployeeAsync(AddManualPayrollEmployeeRequest request, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConfirmPayrollRosterSelectionAsync(ConfirmPayrollRosterSelectionRequest request, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PayrollShadowMonth> CreateSnapshotAsync(int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowMonth> FinalizeAsync(int year, int month, string actor, string? comment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(int year, int month, string? resourceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PayrollShadowReviewAudit>>([]);
        public Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(int year, int month, string resourceId, CancellationToken cancellationToken) => Task.FromResult<PayrollShadowEmployeeDetail?>(null);
        public Task<PayrollRosterPage> GetPayrollRosterAsync(PayrollRosterFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PayrollShadowMonthSummary>>([]);
        public Task<PayrollShadowMonth> RebuildSnapshotAsync(int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResetEligibilityAsync(SetPayrollEligibilityResetRequest request, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetEligibilityAsync(SetPayrollEligibilityRequest request, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetReviewStatusAsync(SetPayrollReviewStatusRequest request, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PayrollShadowMonth> StartReviewAsync(int year, int month, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

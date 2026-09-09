using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollShadowAdminUiGateTests
{
    [Fact]
    public async Task PayrollRoutes_ReturnNotFound_WhenFeatureFlagsOff()
    {
        var service = new FakePayrollShadowService();
        var options = Options.Create(new PayrollShadowOptions());
        var review = Options.Create(new AdminReviewWorkflowOptions { DefaultReviewer = "Ada Admin" });
        var user = new FakeUserContext();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var index = new IndexModel(
            service,
            user,
            options,
            review,
            loggerFactory.CreateLogger<IndexModel>());
        Assert.IsType<NotFoundResult>(await index.OnGetAsync(default));

        var month = new MonthModel(
            service,
            new FakeReviewQueueService(),
            user,
            options,
            review,
            loggerFactory.CreateLogger<MonthModel>())
        {
            Year = 2026,
            Month = 8,
        };
        Assert.IsType<NotFoundResult>(await month.OnGetAsync(default));

        var employee = new EmployeeModel(
            service,
            new FakePayrollActionService(),
            user,
            options,
            Options.Create(new PayrollActionsOptions()),
            review,
            loggerFactory.CreateLogger<EmployeeModel>())
        {
            Year = 2026,
            Month = 8,
            ResourceId = "1",
        };
        Assert.IsType<NotFoundResult>(await employee.OnGetAsync(default));
    }

    private sealed class FakeReviewQueueService : IPayrollReviewQueueService
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

    private sealed class FakePayrollActionService : IPayrollActionService
    {
        public Task CancelAsync(Guid actionId, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollActionProposeResult> UpdateCreateProposalAsync(
            Guid actionId,
            TimeOnly? start,
            TimeOnly? endTime,
            int? mainTaskId,
            string actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollActionExecutionResult> ExecuteAsync(
            Guid actionId, string comment, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollProposedActionRecord?> GetActionAsync(
            Guid actionId, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollProposedActionRecord?>(null);

        public Task<IReadOnlyList<PayrollProposedActionRecord>> ListActionsAsync(
            int year, int month, string? resourceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollProposedActionRecord>>([]);

        public Task<PayrollActionConfirmationView?> PrepareConfirmationAsync(
            Guid actionId, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollActionConfirmationView?>(null);

        public Task<IReadOnlyList<PayrollProposedActionRecord>> ProposeFromFindingsAsync(
            int year, int month, string? resourceId, string actor, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollProposedActionRecord>>([]);

        public Task<PayrollActionProposeResult> ProposeDeleteForPerformanceAsync(
            int year,
            int month,
            string resourceId,
            DateOnly workDate,
            long performanceId,
            string reason,
            string actor,
            PayrollFindingType findingType,
            string? actionKey = null,
            string? sourceFindingKey = null,
            int? sourceFindingId = null,
            IReadOnlyList<string>? sourceFindingKeys = null,
            IReadOnlyList<int>? sourceFindingIds = null,
            string? prestOmschr = null,
            string? prestMemo = null,
            string? bonTechnicianRemark = null,
            string? projectLabel = null,
            string? expectedActivityType = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public AuthenticatedActor? CurrentUser => null;

        public AuthenticatedActor RequireActor(string developmentFallbackReviewer) =>
            new(developmentFallbackReviewer, "sub", developmentFallbackReviewer);
    }

    private sealed class FakePayrollShadowService : IPayrollShadowService
    {
        public Task<PayrollShadowMonth> CreateSnapshotAsync(
            int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollShadowMonth> RebuildSnapshotAsync(int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken, IReadOnlyCollection<string>? limitToResourceIds = null) =>
            throw new NotSupportedException();

        public Task<PayrollMonthPeriodEligibilityInsight> GetPeriodEligibilityInsightAsync(
            int year, int month, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollMonthFinalizationBlockers> GetFinalizationBlockersAsync(
            int year, int month, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplyConfirmedRosterToMonthResult> ApplyConfirmedRosterToMonthAsync(
            int year, int month, string actor, string? comment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollShadowMonth> FinalizeAsync(int year, int month, string actor, string? comment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(
            int year, int month, string? resourceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(
            int year, int month, string resourceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(
            int year, int month, PayrollShadowEmployeeFilter filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResetEligibilityAsync(
            SetPayrollEligibilityResetRequest request, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetEligibilityAsync(
            SetPayrollEligibilityRequest request, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetReviewStatusAsync(
            SetPayrollReviewStatusRequest request, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollShadowMonth> StartReviewAsync(
            int year, int month, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollRosterPage> GetPayrollRosterAsync(
            PayrollRosterFilter filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ConfirmPayrollRosterSelectionAsync(
            ConfirmPayrollRosterSelectionRequest request, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddManualPayrollEmployeeAsync(
            AddManualPayrollEmployeeRequest request, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

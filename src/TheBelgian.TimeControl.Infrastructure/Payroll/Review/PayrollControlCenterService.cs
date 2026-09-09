using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

public sealed class PayrollControlCenterService(
    IPayrollShadowService payrollShadowService,
    IPayrollReviewQueueService reviewQueueService,
    IPayrollActionService payrollActionService,
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IOptions<PayrollActionsOptions> actionsOptions,
    ILogger<PayrollControlCenterService> logger) : IPayrollControlCenterService
{
    public async Task<PayrollControlCenterPage?> GetAsync(
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        var detail = await payrollShadowService.GetMonthDetailAsync(
            year,
            month,
            new PayrollShadowEmployeeFilter(HideExcluded: true, PrioritizeReviewExceptions: false),
            cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var adminQueue = await reviewQueueService.GetAdminQueueAsync(
            year,
            month,
            new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.All),
            cancellationToken);
        var blockers = await payrollShadowService.GetFinalizationBlockersAsync(year, month, cancellationToken);
        var actions = await payrollActionService.ListActionsAsync(year, month, resourceId: null, cancellationToken);
        var months = await payrollShadowService.ListMonthsAsync(cancellationToken);
        var rawFindings = await CountRawFindingsAsync(detail.Month.Id, cancellationToken);
        var createEnabled = actionsOptions.Value.CreatePerformanceEnabled;

        logger.LogInformation(
            "Payroll Control Center built {Year}-{Month:00} adminCases={Admin} actions={Actions} blockers={Blockers}",
            year,
            month,
            adminQueue.AdminCases.Count,
            actions.Count,
            blockers.Blockers.Count);

        return PayrollControlCenterBuilder.Build(
            detail.Month,
            adminQueue,
            blockers,
            actions,
            months,
            createEnabled,
            rawFindings);
    }

    private async Task<int> CountRawFindingsAsync(int shadowMonthId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PayrollFindingRecords.AsNoTracking()
            .CountAsync(item => item.ShadowMonthId == shadowMonthId, cancellationToken);
    }
}

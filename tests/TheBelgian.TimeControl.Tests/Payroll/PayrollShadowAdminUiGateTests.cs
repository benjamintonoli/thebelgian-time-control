using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
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
            user,
            options,
            review,
            loggerFactory.CreateLogger<EmployeeModel>())
        {
            Year = 2026,
            Month = 8,
            ResourceId = "1",
        };
        Assert.IsType<NotFoundResult>(await employee.OnGetAsync(default));
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

        public Task<PayrollShadowMonth> FinalizeAsync(
            int year, int month, string actor, CancellationToken cancellationToken) =>
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
    }
}

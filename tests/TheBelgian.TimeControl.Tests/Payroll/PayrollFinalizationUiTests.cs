using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollFinalizationUiTests
{
    [Fact]
    public async Task FinalizePage_WithOpenCases_DoesNotAllowConfirmWithoutClearance()
    {
        var shadow = new FakeShadow
        {
            Blockers = BlockedOpen(),
            Detail = Detail(),
        };
        var page = new FinalizeModel(
            shadow,
            new FakeUser(),
            Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
            Options.Create(new AdminReviewWorkflowOptions()),
            NullLogger<FinalizeModel>.Instance)
        {
            Year = 2026,
            Month = 8,
            AttestationConfirmed = true,
            Comment = "should not finalize",
        };

        var result = await page.OnPostConfirmAsync(default);
        Assert.IsType<PageResult>(result);
        Assert.Contains("geblokkeerd", page.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, shadow.FinalizeCalls);
    }

    [Fact]
    public async Task FinalizePage_RequiresAttestationCheckbox()
    {
        var shadow = new FakeShadow
        {
            Blockers = Clear(),
            Detail = Detail(),
        };
        var page = new FinalizeModel(
            shadow,
            new FakeUser(),
            Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
            Options.Create(new AdminReviewWorkflowOptions()),
            NullLogger<FinalizeModel>.Instance)
        {
            Year = 2026,
            Month = 8,
            AttestationConfirmed = false,
        };

        var result = await page.OnPostConfirmAsync(default);
        Assert.IsType<PageResult>(result);
        Assert.Contains("Bevestig expliciet", page.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, shadow.FinalizeCalls);
    }

    [Fact]
    public async Task FinalizePage_WhenClear_FinalizesWithComment()
    {
        var shadow = new FakeShadow
        {
            Blockers = Clear(),
            Detail = Detail(),
        };
        var page = new FinalizeModel(
            shadow,
            new FakeUser(),
            Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
            Options.Create(new AdminReviewWorkflowOptions()),
            NullLogger<FinalizeModel>.Instance)
        {
            Year = 2026,
            Month = 8,
            AttestationConfirmed = true,
            Comment = "maand ok",
        };

        var result = await page.OnPostConfirmAsync(default);
        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(1, shadow.FinalizeCalls);
        Assert.Equal("maand ok", shadow.LastComment);
    }

    [Fact]
    public void PendingAlone_IsNotListedAsBlocker()
    {
        var blockers = Clear() with { PendingIncluded = 44 };
        Assert.True(blockers.CanFinalize);
        Assert.DoesNotContain(blockers.SummaryLines, line => line.Contains("Pending", StringComparison.OrdinalIgnoreCase));
    }

    private static PayrollShadowMonthDetail Detail() =>
        new(
            new PayrollShadowMonth
            {
                Year = 2026,
                Month = 8,
                Status = PayrollShadowMonthStatus.InReview,
                PeriodStart = new DateOnly(2026, 8, 1),
                PeriodEnd = new DateOnly(2026, 8, 31),
                EvaluationDate = new DateOnly(2026, 9, 1),
                CalculationVersion = "t",
                ConfigurationSnapshotJson = "{}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = "t",
            },
            new PayrollShadowMonthSummary(
                2026,
                8,
                PayrollShadowMonthStatus.InReview,
                DateTimeOffset.UtcNow,
                new DateOnly(2026, 9, 1),
                "t",
                44,
                44,
                0,
                0,
                44,
                0,
                0),
            [],
            0);

    private static PayrollMonthFinalizationBlockers Clear() =>
        new(
            true,
            44,
            0,
            0,
            0,
            44,
            0,
            0,
            0,
            108,
            0,
            0,
            108,
            0,
            [],
            new PayrollMonthFinancialSummary(1, 2, 3, 4, 5),
            "v",
            true,
            []);

    private static PayrollMonthFinalizationBlockers BlockedOpen() =>
        Clear() with
        {
            CanFinalize = false,
            OpenReviewCases = 108,
            ReviewedReviewCases = 0,
            Blockers =
            [
                new("OPEN_REVIEW_CASES", 108, "108 openstaande payrollcontroles."),
            ],
            SummaryLines = ["108 openstaande payrollcontroles."],
        };

    private sealed class FakeUser : ICurrentUserContext
    {
        public AuthenticatedActor? CurrentUser => new("Ada Admin", "sub", "Ada Admin");
        public AuthenticatedActor RequireActor(string developmentFallbackReviewer) =>
            new(developmentFallbackReviewer, "sub", developmentFallbackReviewer);
    }

    private sealed class FakeShadow : IPayrollShadowService
    {
        public PayrollMonthFinalizationBlockers Blockers { get; set; } = Clear();
        public PayrollShadowMonthDetail? Detail { get; set; }
        public int FinalizeCalls { get; private set; }
        public string? LastComment { get; private set; }

        public Task<PayrollMonthFinalizationBlockers> GetFinalizationBlockersAsync(
            int year, int month, CancellationToken cancellationToken) =>
            Task.FromResult(Blockers);

        public Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(
            int year, int month, PayrollShadowEmployeeFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(Detail);

        public Task<PayrollShadowMonth> FinalizeAsync(
            int year, int month, string actor, string? comment, CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            LastComment = comment;
            return Task.FromResult(Detail!.Month);
        }

        public Task<PayrollShadowMonth> CreateSnapshotAsync(int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowMonth> RebuildSnapshotAsync(int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken, IReadOnlyCollection<string>? limitToResourceIds = null) => throw new NotSupportedException();
        public Task<PayrollMonthPeriodEligibilityInsight> GetPeriodEligibilityInsightAsync(int year, int month, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplyConfirmedRosterToMonthResult> ApplyConfirmedRosterToMonthAsync(int year, int month, string actor, string? comment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(int year, int month, string? resourceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PayrollShadowReviewAudit>>([]);
        public Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(int year, int month, string resourceId, CancellationToken cancellationToken) => Task.FromResult<PayrollShadowEmployeeDetail?>(null);
        public Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResetEligibilityAsync(SetPayrollEligibilityResetRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEligibilityAsync(SetPayrollEligibilityRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetReviewStatusAsync(SetPayrollReviewStatusRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowMonth> StartReviewAsync(int year, int month, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollRosterPage> GetPayrollRosterAsync(PayrollRosterFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfirmPayrollRosterSelectionAsync(ConfirmPayrollRosterSelectionRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddManualPayrollEmployeeAsync(AddManualPayrollEmployeeRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

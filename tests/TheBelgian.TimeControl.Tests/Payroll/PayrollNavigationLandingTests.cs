using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollNavigationLandingTests
{
    [Fact]
    public void SelectCanonical_PrefersInReview_ThenReady_ThenNonFinalized()
    {
        var months = new List<PayrollShadowMonthSummary>
        {
            Summary(2026, 7, PayrollShadowMonthStatus.Finalized),
            Summary(2026, 8, PayrollShadowMonthStatus.InReview),
            Summary(2026, 9, PayrollShadowMonthStatus.ReadyForReview),
        };

        var canonical = PayrollShadowPeriodSelection.SelectCanonical(months);
        Assert.NotNull(canonical);
        Assert.Equal(8, canonical!.Month);
        Assert.Equal(PayrollShadowMonthStatus.InReview, canonical.Status);
    }

    [Fact]
    public void SelectCanonical_WhenOnlyFinalized_UsesLatest()
    {
        var months = new List<PayrollShadowMonthSummary>
        {
            Summary(2026, 8, PayrollShadowMonthStatus.Finalized),
            Summary(2026, 7, PayrollShadowMonthStatus.Finalized),
        };

        var canonical = PayrollShadowPeriodSelection.SelectCanonical(months);
        Assert.Equal(8, canonical!.Month);
    }

    [Fact]
    public void SelectCanonical_Empty_ReturnsNull()
    {
        Assert.Null(PayrollShadowPeriodSelection.SelectCanonical([]));
    }

    [Fact]
    public void PayrollIndex_HasCanonicalRoute()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "Index.cshtml"));
        Assert.Contains("@page \"/Admin/Payroll\"", markup, StringComparison.Ordinal);
        Assert.Contains("./Workbench", markup, StringComparison.Ordinal);
        Assert.Contains("./Queue", markup, StringComparison.Ordinal);
        Assert.Contains("./Finalize", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_PayrollShadow_TargetsIndexPage_NotBareFolderGuess()
    {
        var layout = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Shared",
            "_Layout.cshtml"));
        Assert.Contains("asp-page=\"/Admin/Payroll/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Admin/Payroll/Employees\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Admin/Payroll\"", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionConfirm_SuccessRedirectsToWorkbenchPage()
    {
        var code = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "ActionConfirm.cshtml.cs"));
        Assert.Contains("RedirectToPage(\"./Workbench\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Redirect(\"/Admin/Payroll\")", code, StringComparison.Ordinal);
        Assert.DoesNotContain("RedirectToPage(\"./Index\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void VisiblePayrollPages_DeclareResolvableRoutes()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Index.cshtml"] = "@page \"/Admin/Payroll\"",
            ["Employees.cshtml"] = "@page \"/Admin/Payroll/Employees\"",
            ["Month.cshtml"] = "@page \"/Admin/Payroll/{year:int}/{month:int}\"",
            ["Queue.cshtml"] = "@page \"/Admin/Payroll/{year:int}/{month:int}/Queue\"",
            ["Workbench.cshtml"] = "@page \"/Admin/Payroll/{year:int}/{month:int}/Workbench/Project300\"",
            ["Finalize.cshtml"] = "@page \"/Admin/Payroll/{year:int}/{month:int}/Finalize\"",
            ["ActionConfirm.cshtml"] = "@page \"/Admin/Payroll/ActionConfirm\"",
            ["Employee.cshtml"] = "@page \"/Admin/Payroll/{year:int}/{month:int}/{resourceId}\"",
            ["Guided.cshtml"] = "@page \"/Admin/Payroll/{year:int}/{month:int}/Guided/{category}\"",
        };

        foreach (var (file, route) in expected)
        {
            var markup = File.ReadAllText(Path.Combine(root, file));
            Assert.Contains(route, markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Index_Get_RedirectsToCanonicalMonth()
    {
        var service = new FakeShadow
        {
            Months =
            [
                Summary(2026, 8, PayrollShadowMonthStatus.InReview),
            ],
        };
        var page = CreateIndex(service);
        var result = await page.OnGetAsync(default);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("./Month", redirect.PageName);
        Assert.Equal(2026, redirect.RouteValues?["year"]);
        Assert.Equal(8, redirect.RouteValues?["month"]);
    }

    [Fact]
    public async Task Index_Get_WithStay_ShowsListWithoutRedirect()
    {
        var service = new FakeShadow
        {
            Months =
            [
                Summary(2026, 8, PayrollShadowMonthStatus.InReview),
            ],
        };
        var page = CreateIndex(service);
        page.Stay = true;
        var result = await page.OnGetAsync(default);
        Assert.IsType<PageResult>(result);
        Assert.Single(page.Months);
        Assert.Equal(8, page.CanonicalMonth!.Month);
    }

    [Fact]
    public async Task Index_Get_WhenFlagsOff_ReturnsNotFound()
    {
        var page = CreateIndex(new FakeShadow(), flagsOn: false);
        Assert.IsType<NotFoundResult>(await page.OnGetAsync(default));
    }

    private static IndexModel CreateIndex(FakeShadow service, bool flagsOn = true) =>
        new(
            service,
            new FakeUser(),
            Options.Create(new PayrollShadowOptions { Enabled = flagsOn, AdminUiEnabled = flagsOn }),
            Options.Create(new AdminReviewWorkflowOptions { DefaultReviewer = "benjamin.tonoli@thebelgian.be" }),
            NullLogger<IndexModel>.Instance);

    private static PayrollShadowMonthSummary Summary(int year, int month, PayrollShadowMonthStatus status) =>
        new(year, month, status, DateTimeOffset.UtcNow, new DateOnly(year, month, 1).AddMonths(1),
            "v", 1, 1, 0, 0, 0, 0, 0);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    private sealed class FakeUser : ICurrentUserContext
    {
        public AuthenticatedActor? CurrentUser => new("benjamin.tonoli@thebelgian.be", "sub", "Benjamin");
        public AuthenticatedActor RequireActor(string developmentFallbackReviewer) => CurrentUser!;
    }

    private sealed class FakeShadow : IPayrollShadowService
    {
        public IReadOnlyList<PayrollShadowMonthSummary> Months { get; set; } = [];

        public Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Months);

        public Task ConfirmPayrollRosterSelectionAsync(
            ConfirmPayrollRosterSelectionRequest request, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddManualPayrollEmployeeAsync(
            AddManualPayrollEmployeeRequest request, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PayrollRosterPage> GetPayrollRosterAsync(
            PayrollRosterFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(new PayrollRosterPage(
                filter.AsOfDate ?? new DateOnly(2026, 9, 3),
                [],
                0,
                0,
                0,
                0));

        public Task<PayrollShadowMonth> CreateSnapshotAsync(
            int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollShadowMonth> RebuildSnapshotAsync(
            int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) =>
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

        public Task<PayrollShadowMonth> FinalizeAsync(
            int year, int month, string actor, string? comment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(
            int year, int month, string? resourceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollShadowReviewAudit>>([]);

        public Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(
            int year, int month, string resourceId, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollShadowEmployeeDetail?>(null);

        public Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(
            int year, int month, PayrollShadowEmployeeFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollShadowMonthDetail?>(null);

        public Task ResetEligibilityAsync(
            SetPayrollEligibilityResetRequest request, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetEligibilityAsync(
            SetPayrollEligibilityRequest request, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetReviewStatusAsync(
            SetPayrollReviewStatusRequest request, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PayrollShadowMonth> StartReviewAsync(
            int year, int month, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

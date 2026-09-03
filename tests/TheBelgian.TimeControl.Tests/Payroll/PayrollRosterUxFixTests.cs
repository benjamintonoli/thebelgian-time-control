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

public sealed class PayrollRosterUxFixTests
{
    [Fact]
    public void Splitter_UncheckedRow_IsExcluded()
    {
        var (included, excluded) = PayrollRosterSelectionSplitter.Split(
        [
            new PayrollRosterSelectionRow { ResourceId = "476", IsOnPayroll = false },
        ]);
        Assert.Empty(included);
        Assert.Equal(["476"], excluded);
    }

    [Fact]
    public void Splitter_CheckedRow_IsIncluded()
    {
        var (included, excluded) = PayrollRosterSelectionSplitter.Split(
        [
            new PayrollRosterSelectionRow { ResourceId = "10", IsOnPayroll = true },
        ]);
        Assert.Equal(["10"], included);
        Assert.Empty(excluded);
    }

    [Fact]
    public void Splitter_IgnoresEmployeesNotInSubmittedRows()
    {
        var (included, excluded) = PayrollRosterSelectionSplitter.Split(
        [
            new PayrollRosterSelectionRow { ResourceId = "476", IsOnPayroll = false },
        ]);
        Assert.DoesNotContain("10", included);
        Assert.DoesNotContain("10", excluded);
        Assert.Equal(["476"], excluded);
    }

    [Fact]
    public async Task EmployeesConfirm_PostsExplicitUnchecked_AsExcluded()
    {
        var service = new RecordingPayrollShadowService();
        var page = CreateEmployeesPage(service, flagsOn: true);
        page.ValidFrom = new DateOnly(2026, 9, 3);
        page.ReasonCode = "RosterConfirmation";
        page.Rows =
        [
            new PayrollRosterSelectionRow { ResourceId = "476", IsOnPayroll = false },
            new PayrollRosterSelectionRow { ResourceId = "10", IsOnPayroll = true },
        ];

        var result = await page.OnPostConfirmAsync(default);
        Assert.IsType<PageResult>(result);
        Assert.NotNull(service.LastConfirm);
        Assert.Equal(new DateOnly(2026, 9, 3), service.LastConfirm!.ValidFrom);
        Assert.Equal(["10"], service.LastConfirm.IncludedResourceIds);
        Assert.Equal(["476"], service.LastConfirm.ExcludedResourceIds);
        Assert.Contains("1 inbegrepen, 1 uitgesloten", page.Message);
        Assert.Null(page.Error);
    }

    [Fact]
    public async Task EmployeesConfirm_OnFailure_DoesNotClearPostedRows()
    {
        var service = new RecordingPayrollShadowService { ThrowOnConfirm = true };
        var page = CreateEmployeesPage(service, flagsOn: true);
        page.ValidFrom = new DateOnly(2026, 9, 3);
        page.ReasonCode = "RosterConfirmation";
        page.Rows = [new PayrollRosterSelectionRow { ResourceId = "476", IsOnPayroll = false }];

        var result = await page.OnPostConfirmAsync(default);
        Assert.IsType<PageResult>(result);
        Assert.False(string.IsNullOrWhiteSpace(page.Error));
        Assert.Null(page.Message);
        Assert.Single(page.Rows);
        Assert.Equal("476", page.Rows[0].ResourceId);
        Assert.False(page.Rows[0].IsOnPayroll);
        Assert.Equal(new DateOnly(2026, 9, 3), page.ValidFrom);
    }

    [Fact]
    public async Task PayrollIndexAndEmployees_Succeed_WhenFlagsOn()
    {
        var service = new RecordingPayrollShadowService();
        var options = Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true });
        var review = Options.Create(new AdminReviewWorkflowOptions { DefaultReviewer = "Ada Admin" });
        var user = new FakeUserContext();

        var index = new IndexModel(
            service,
            user,
            options,
            review,
            NullLogger<IndexModel>.Instance);
        Assert.IsType<PageResult>(await index.OnGetAsync(default));

        var employees = CreateEmployeesPage(service, flagsOn: true);
        Assert.IsType<PageResult>(await employees.OnGetAsync(default));
    }

    [Fact]
    public async Task EmployeesRoute_ReturnsNotFound_WhenFlagsOff()
    {
        var page = CreateEmployeesPage(new RecordingPayrollShadowService(), flagsOn: false);
        Assert.IsType<NotFoundResult>(await page.OnGetAsync(default));
    }

    [Fact]
    public void Layout_PayrollShadowLink_TargetsIndexPage()
    {
        var layout = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Shared", "_Layout.cshtml"));
        Assert.Contains("asp-page=\"/Admin/Payroll/Index\"", layout);
        Assert.Contains("Payroll shadow", layout);
        Assert.DoesNotContain("asp-page=\"/Admin/Payroll\"", layout);
        Assert.Contains("asp-page=\"/Admin/Payroll/Employees\"", layout);
    }

    [Fact]
    public void EmployeesPage_LinksToPayrollIndex_AndBindsExplicitRowState()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "Employees.cshtml"));
        Assert.Contains("asp-page=\"/Admin/Payroll/Index\"", markup);
        Assert.Contains("Rows[@i].ResourceId", markup);
        Assert.Contains("Rows[@i].IsOnPayroll", markup);
        Assert.Contains("value=\"false\"", markup);
        Assert.Contains("value=\"true\"", markup);
        Assert.Contains("In payrollcontrole", markup);
        Assert.DoesNotContain("SelectedResourceIds", markup);
        Assert.DoesNotContain("VisibleResourceIds", markup);
    }

    private static EmployeesModel CreateEmployeesPage(IPayrollShadowService service, bool flagsOn) =>
        new(
            service,
            new EmptyResourceReader(),
            new FakeUserContext(),
            Options.Create(new PayrollShadowOptions
            {
                Enabled = flagsOn,
                AdminUiEnabled = flagsOn,
            }),
            Options.Create(new AdminReviewWorkflowOptions { DefaultReviewer = "Ada Admin" }),
            NullLogger<EmployeesModel>.Instance);

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

    private sealed class EmptyResourceReader : IPayrollResourceReader
    {
        public Task<IReadOnlyList<PayrollEmployeeCandidate>> ReadCandidatesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollEmployeeCandidate>>([]);
    }

    private sealed class RecordingPayrollShadowService : IPayrollShadowService
    {
        public ConfirmPayrollRosterSelectionRequest? LastConfirm { get; private set; }
        public bool ThrowOnConfirm { get; set; }

        public Task ConfirmPayrollRosterSelectionAsync(
            ConfirmPayrollRosterSelectionRequest request,
            string actor,
            CancellationToken cancellationToken)
        {
            LastConfirm = request;
            if (ThrowOnConfirm)
            {
                throw new InvalidOperationException("Simulated confirmation failure.");
            }

            return Task.CompletedTask;
        }

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

        public Task<PayrollShadowMonth> FinalizeAsync(
            int year, int month, string actor, CancellationToken cancellationToken) =>
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

        public Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollShadowMonthSummary>>([]);

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

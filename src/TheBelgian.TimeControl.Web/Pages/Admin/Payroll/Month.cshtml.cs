using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class MonthModel(
    IPayrollShadowService payrollShadowService,
    IPayrollReviewQueueService reviewQueueService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<MonthModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollEligibilityStatus? Eligibility { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollEmployeeReviewStatus? Review { get; set; }
    [BindProperty(SupportsGet = true)] public bool NeedsDecisionOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool NeedsFollowUpOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool MissingAcertaIdentityOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool NegativeDifferenceOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool NonzeroStandbyOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool LargeAbsoluteDifferenceOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool HasFindingsOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool HighFindingsOnly { get; set; }
    [BindProperty(SupportsGet = true)] public string? FindingFilter { get; set; }

    public PayrollShadowMonthDetail? Detail { get; private set; }
    public PayrollReviewQueueSummary? QueueSummary { get; private set; }
    public int HighOpenCases { get; private set; }
    public int EmployeesWithOpenIssues { get; private set; }
    public int EmployeesWithoutOpenIssues { get; private set; }
    public PayrollMonthPeriodEligibilityInsight? PeriodInsight { get; private set; }
    public PayrollMonthFinalizationBlockers? FinalizationBlockers { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostStartReviewAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await payrollShadowService.StartReviewAsync(Year, Month, RequireActor().AuditIdentity, cancellationToken);
            Message = "Review gestart. Er wordt niets naar Acerta of Plenion verstuurd.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll shadow review start failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostApplyRosterAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var result = await payrollShadowService.ApplyConfirmedRosterToMonthAsync(
                Year,
                Month,
                RequireActor().AuditIdentity,
                comment: null,
                cancellationToken);
            Message =
                $"Payrolllijst toegepast vanaf {result.AppliedFrom:dd/MM/yyyy}: {result.IncludedWritten} Included, {result.ExcludedWritten} Excluded ({result.SkippedAlreadyEffective} al effectief). Herbereken de snapshot om August te vernieuwen.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll roster apply-to-month failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRebuildAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var existing = await payrollShadowService.GetMonthDetailAsync(
                Year,
                Month,
                new PayrollShadowEmployeeFilter(HideExcluded: false, PrioritizeReviewExceptions: false),
                cancellationToken);
            var evaluationDate = existing?.Month.EvaluationDate
                ?? new DateOnly(Year, Month, 1).AddMonths(1);

            await payrollShadowService.RebuildSnapshotAsync(
                Year,
                Month,
                evaluationDate,
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = "Shadow-maand herberekend. Bestaande reviewbeslissingen zijn bewaard waar mogelijk.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll shadow rebuild failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Detail = await payrollShadowService.GetMonthDetailAsync(
            Year,
            Month,
            new PayrollShadowEmployeeFilter(
                Eligibility,
                Review,
                NeedsDecisionOnly,
                NeedsFollowUpOnly,
                MissingAcertaIdentityOnly,
                NegativeDifferenceOnly,
                NonzeroStandbyOnly,
                HideExcluded: true,
                LargeAbsoluteDifferenceOnly,
                PrioritizeReviewExceptions: true,
                HasFindingsOnly,
                HighFindingsOnly,
                FindingType: null,
                FindingFamily: string.IsNullOrWhiteSpace(FindingFilter) ? null : FindingFilter.Trim()),
            cancellationToken);
        if (Detail is null)
        {
            PeriodInsight = null;
            FinalizationBlockers = null;
            QueueSummary = null;
            HighOpenCases = 0;
            EmployeesWithOpenIssues = 0;
            EmployeesWithoutOpenIssues = 0;
            return;
        }

        PeriodInsight = await payrollShadowService.GetPeriodEligibilityInsightAsync(Year, Month, cancellationToken);
        FinalizationBlockers = await payrollShadowService.GetFinalizationBlockersAsync(Year, Month, cancellationToken);

        try
        {
            var queue = await reviewQueueService.GetQueueAsync(
                Year,
                Month,
                new PayrollReviewQueueFilter(),
                cancellationToken);
            QueueSummary = queue.Summary;
            HighOpenCases = queue.Cases.Count(item =>
                item.Severity == PayrollFindingSeverity.High
                && item.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp);
            EmployeesWithOpenIssues = queue.EmployeesWithOpenIssues.Count;
            EmployeesWithoutOpenIssues = queue.EmployeesWithoutOpenIssues.Count;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll review queue summary failed for {Year}-{Month:00}.", Year, Month);
            QueueSummary = null;
            HighOpenCases = 0;
            EmployeesWithOpenIssues = 0;
            EmployeesWithoutOpenIssues = 0;
        }
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class MonthModel(
    IPayrollShadowService payrollShadowService,
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

    public PayrollShadowMonthDetail? Detail { get; private set; }
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
            Message = "Review gestart.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll shadow review start failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostFinalizeAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await payrollShadowService.FinalizeAsync(Year, Month, RequireActor().AuditIdentity, cancellationToken);
            Message = "Shadow-maand afgesloten.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll shadow finalize failed.");
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
                NonzeroStandbyOnly),
            cancellationToken);
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class FinalizeModel(
    IPayrollShadowService payrollShadowService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<FinalizeModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }

    [BindProperty] public bool AttestationConfirmed { get; set; }
    [BindProperty] public string? Comment { get; set; }

    public PayrollShadowMonthDetail? Detail { get; private set; }
    public PayrollMonthFinalizationBlockers? Blockers { get; private set; }
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

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        if (Detail is null || Blockers is null)
        {
            return NotFound();
        }

        if (!Blockers.CanFinalize)
        {
            Error = "Afsluiten geblokkeerd: " + string.Join(" · ", Blockers.SummaryLines);
            return Page();
        }

        if (!AttestationConfirmed)
        {
            Error = "Bevestig expliciet dat de openstaande payrollcontroles zijn afgehandeld.";
            return Page();
        }

        try
        {
            await payrollShadowService.FinalizeAsync(
                Year,
                Month,
                RequireActor().AuditIdentity,
                Comment,
                cancellationToken);
            return RedirectToPage("./Month", new { year = Year, month = Month });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll month finalization failed for {Year}-{Month:00}.", Year, Month);
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Detail = await payrollShadowService.GetMonthDetailAsync(
            Year,
            Month,
            new PayrollShadowEmployeeFilter(HideExcluded: false, PrioritizeReviewExceptions: false),
            cancellationToken);
        if (Detail is null)
        {
            Blockers = null;
            return;
        }

        Blockers = await payrollShadowService.GetFinalizationBlockersAsync(Year, Month, cancellationToken);
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

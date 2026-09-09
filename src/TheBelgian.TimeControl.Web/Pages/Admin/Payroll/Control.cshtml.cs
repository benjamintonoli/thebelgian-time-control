using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class ControlModel(
    IPayrollControlCenterService controlCenterService,
    IOptions<PayrollShadowOptions> payrollOptions,
    ILogger<ControlModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Year { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Month { get; set; }

    public PayrollControlCenterPage? Center { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            Center = await controlCenterService.GetAsync(Year, Month, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll Control Center failed for {Year}-{Month:00}.", Year, Month);
            Error = "Control Center kon niet worden geladen.";
        }

        return Page();
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;
}

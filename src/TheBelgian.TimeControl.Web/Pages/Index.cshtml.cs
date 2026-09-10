using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Web.Pages;

public class IndexModel(
    IPayrollShadowService payrollShadowService,
    IPayrollControlCenterService controlCenterService,
    ILogger<IndexModel> logger) : PageModel
{
    public PayrollControlCenterPage? Center { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var months = await payrollShadowService.ListMonthsAsync(cancellationToken);
            var canonical = PayrollShadowPeriodSelection.SelectCanonical(months);
            if (canonical is null)
            {
                Center = null;
                return;
            }

            Center = await controlCenterService.GetAsync(canonical.Year, canonical.Month, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Home dashboard kon looncontrole-status niet laden.");
            Error = "Looncontrole-overzicht tijdelijk niet beschikbaar.";
            Center = null;
        }
    }
}

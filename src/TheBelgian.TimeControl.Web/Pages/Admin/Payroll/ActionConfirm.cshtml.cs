using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class ActionConfirmModel(
    IPayrollActionService payrollActionService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<PayrollActionsOptions> actionsOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<ActionConfirmModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid ActionId { get; set; }

    [BindProperty]
    public string Comment { get; set; } = string.Empty;

    public PayrollActionConfirmationView? View { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        View = await payrollActionService.PrepareConfirmationAsync(ActionId, cancellationToken);
        if (View is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(Comment))
        {
            Comment = View.DefaultComment;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var actor = RequireActor();
            var result = await payrollActionService.ExecuteAsync(
                ActionId,
                Comment,
                actor.AuditIdentity,
                cancellationToken);
            if (result.Status == PayrollProposedActionStatus.Applied)
            {
                var confirmation = await payrollActionService.PrepareConfirmationAsync(ActionId, cancellationToken);
                return RedirectToPage("./Employee", new
                {
                    year = confirmation?.Year,
                    month = confirmation?.Month,
                    resourceId = confirmation?.ResourceId,
                });
            }

            Error = result.Message;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll action execute failed for {ActionId}.", ActionId);
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCancelAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var confirmation = await payrollActionService.PrepareConfirmationAsync(ActionId, cancellationToken);
            await payrollActionService.CancelAsync(ActionId, RequireActor().AuditIdentity, cancellationToken);
            if (confirmation is not null)
            {
                return RedirectToPage("./Employee", new
                {
                    year = confirmation.Year,
                    month = confirmation.Month,
                    resourceId = confirmation.ResourceId,
                });
            }

            Message = "Actie geannuleerd.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll action cancel failed for {ActionId}.", ActionId);
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled
        && payrollOptions.Value.AdminUiEnabled
        && actionsOptions.Value.Enabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Review;
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

        if (ActionId == Guid.Empty)
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

        if (ActionId == Guid.Empty)
        {
            Error = "Actie niet gevonden. Open de bevestiging opnieuw via de workbench.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Comment))
        {
            Error = "Reden is verplicht.";
            View = await payrollActionService.PrepareConfirmationAsync(ActionId, cancellationToken);
            return Page();
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
                TempData["FlashSuccess"] = BuildSuccessMessage(confirmation);
                return RedirectToPage("./Workbench", new
                {
                    year = confirmation?.Year,
                    month = confirmation?.Month,
                    Search = confirmation?.DisplayName ?? confirmation?.ResourceId,
                    Scope = PayrollReviewQueueScope.Open,
                });
            }

            Error = MapExecutionFailure(result);
            logger.LogWarning(
                "Payroll action {ActionId} execute ended as {Status}: {Message}",
                ActionId,
                result.Status,
                result.Message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll action execute failed for {ActionId}.", ActionId);
            Error = MapException(exception);
        }

        View = await payrollActionService.PrepareConfirmationAsync(ActionId, cancellationToken);
        if (View is null)
        {
            Error ??= "De prestatie kon niet worden verwijderd. Er is niets gewijzigd.";
            return Page();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        if (ActionId == Guid.Empty)
        {
            return NotFound();
        }

        try
        {
            var confirmation = await payrollActionService.PrepareConfirmationAsync(ActionId, cancellationToken);
            await payrollActionService.CancelAsync(ActionId, RequireActor().AuditIdentity, cancellationToken);
            if (confirmation is not null)
            {
                return RedirectToPage("./Workbench", new
                {
                    year = confirmation.Year,
                    month = confirmation.Month,
                    Search = confirmation.DisplayName ?? confirmation.ResourceId,
                    Scope = PayrollReviewQueueScope.Open,
                });
            }

            Message = "Actie geannuleerd.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll action cancel failed for {ActionId}.", ActionId);
            Error = "Annuleren mislukt. Probeer opnieuw via de workbench.";
        }

        return await OnGetAsync(cancellationToken);
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled
        && payrollOptions.Value.AdminUiEnabled
        && actionsOptions.Value.Enabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);

    private static string BuildSuccessMessage(PayrollActionConfirmationView? confirmation)
    {
        var delete = confirmation?.DeleteProposal;
        if (delete is not null)
        {
            return $"Prestatie {delete.CurrentStart:HH:mm}–{delete.CurrentEnd:HH:mm} is verwijderd uit Plenion.";
        }

        return "Actie uitgevoerd.";
    }

    private static string MapExecutionFailure(PayrollActionExecutionResult result)
    {
        if (result.Status == PayrollProposedActionStatus.Stale)
        {
            return "De prestatie is ondertussen gewijzigd. Controleer de gegevens opnieuw.";
        }

        var detail = result.Message ?? string.Empty;
        if (detail.Contains("gekoppelde", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("afhankelijkheid", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("dependencies", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("PROJ_CREDIT", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("PROJ_VERGOED", StringComparison.OrdinalIgnoreCase))
        {
            return "Deze prestatie heeft gekoppelde gegevens en kan niet automatisch worden verwijderd.";
        }

        if (detail.Contains("uitgeschakeld", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("DeletePerformanceEnabled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ExecutionEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Verwijderen is momenteel uitgeschakeld.";
        }

        return "De prestatie kon niet worden verwijderd. Er is niets gewijzigd.";
    }

    private static string MapException(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("verplicht", StringComparison.OrdinalIgnoreCase))
        {
            return "Reden is verplicht.";
        }

        if (message.Contains("uitgeschakeld", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ExecutionEnabled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DeletePerformanceEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Verwijderen is momenteel uitgeschakeld.";
        }

        if (message.Contains("niet gevonden", StringComparison.OrdinalIgnoreCase))
        {
            return "Actie niet gevonden. Open de bevestiging opnieuw via de workbench.";
        }

        return "De prestatie kon niet worden verwijderd. Er is niets gewijzigd.";
    }
}

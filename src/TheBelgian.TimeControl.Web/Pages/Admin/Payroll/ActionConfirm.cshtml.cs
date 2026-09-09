using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class ActionConfirmModel(
    IPayrollActionService payrollActionService,
    IPayrollProject300WorkbenchService project300WorkbenchService,
    IPayrollProject200WorkbenchService project200WorkbenchService,
    IPayrollProject100WorkbenchService project100WorkbenchService,
    IPayrollStandbyWorkbenchService standbyWorkbenchService,
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

    [BindProperty]
    public TimeOnly? EditStart { get; set; }

    [BindProperty]
    public TimeOnly? EditEnd { get; set; }

    [BindProperty]
    public int? EditMainTaskId { get; set; }

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

        if (View.CreateProposal is not null)
        {
            EditStart ??= TimeOnly.FromTimeSpan(View.CreateProposal.Start.TimeOfDay);
            EditEnd ??= TimeOnly.FromTimeSpan(View.CreateProposal.End.TimeOfDay);
            EditMainTaskId ??= View.CreateProposal.MainTaskId;
        }
        else if (View.Evidence.SuggestedPayableStart is not null && View.Evidence.SuggestedPayableEnd is not null)
        {
            EditStart ??= TimeOnly.FromTimeSpan(View.Evidence.SuggestedPayableStart.Value.TimeOfDay);
            EditEnd ??= TimeOnly.FromTimeSpan(View.Evidence.SuggestedPayableEnd.Value.TimeOfDay);
            EditMainTaskId ??= PayrollActionEligibility.ParseSuggestedHfdTaakId(View.Evidence.Evidence);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateCreateAsync(CancellationToken cancellationToken)
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
            var actor = RequireActor();
            var result = await payrollActionService.UpdateCreateProposalAsync(
                ActionId,
                EditStart,
                EditEnd,
                EditMainTaskId,
                actor.AuditIdentity,
                cancellationToken);
            if (!result.Ok)
            {
                Error = result.Message;
            }
            else
            {
                Message = result.Message;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Create proposal update failed for {ActionId}.", ActionId);
            Error = "Voorstel bijwerken mislukt.";
        }

        return await OnGetAsync(cancellationToken);
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
                if (IsStandbyAction(confirmation))
                {
                    standbyWorkbenchService.InvalidateQueueCache(
                        confirmation?.Year ?? 0,
                        confirmation?.Month ?? 0);
                    var nextStandbyFocus = await ResolveNextStandbyFocusAsync(
                        confirmation?.Year,
                        confirmation?.Month,
                        cancellationToken);
                    return RedirectToPage("./StandbyWorkbench", new
                    {
                        year = confirmation?.Year,
                        month = confirmation?.Month,
                        Scope = PayrollReviewQueueScope.Open,
                        Focus = nextStandbyFocus,
                    });
                }

                var isProject100 = IsProject100Action(confirmation);
                if (isProject100)
                {
                    project100WorkbenchService.InvalidateQueueCache(
                        confirmation?.Year ?? 0,
                        confirmation?.Month ?? 0);
                    var nextP100Focus = await ResolveNextProject100FocusAsync(
                        confirmation?.Year,
                        confirmation?.Month,
                        cancellationToken);
                    return RedirectToPage("./Project100Workbench", new
                    {
                        year = confirmation?.Year,
                        month = confirmation?.Month,
                        Scope = PayrollReviewQueueScope.Open,
                        Focus = nextP100Focus,
                    });
                }

                var isProject200 = IsProject200Action(confirmation);
                if (isProject200)
                {
                    project200WorkbenchService.InvalidateQueueCache(
                        confirmation?.Year ?? 0,
                        confirmation?.Month ?? 0);
                    var nextFocus = await ResolveNextProject200FocusAsync(
                        confirmation?.Year,
                        confirmation?.Month,
                        cancellationToken);
                    return RedirectToPage("./Project200Workbench", new
                    {
                        year = confirmation?.Year,
                        month = confirmation?.Month,
                        Scope = PayrollReviewQueueScope.Open,
                        Focus = nextFocus,
                    });
                }

                project300WorkbenchService.InvalidateQueueCache(
                    confirmation?.Year ?? 0,
                    confirmation?.Month ?? 0);
                var nextProject300Focus = await ResolveNextProject300FocusAsync(
                    confirmation?.Year,
                    confirmation?.Month,
                    cancellationToken);
                return RedirectToPage("./Workbench", new
                {
                    year = confirmation?.Year,
                    month = confirmation?.Month,
                    Scope = PayrollReviewQueueScope.Open,
                    Focus = nextProject300Focus,
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
                var workbenchPage = IsStandbyAction(confirmation)
                    ? "./StandbyWorkbench"
                    : IsProject100Action(confirmation)
                        ? "./Project100Workbench"
                        : IsProject200Action(confirmation)
                            ? "./Project200Workbench"
                            : "./Workbench";
                return RedirectToPage(workbenchPage, new
                {
                    year = confirmation.Year,
                    month = confirmation.Month,
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

    private async Task<string?> ResolveNextProject300FocusAsync(
        int? year,
        int? month,
        CancellationToken cancellationToken)
    {
        if (year is null or <= 0 || month is null or <= 0)
        {
            return null;
        }

        try
        {
            var shell = await project300WorkbenchService.GetShellAsync(
                year.Value,
                month.Value,
                new PayrollReviewQueueFilter(
                    PayrollReviewCategory.Project300,
                    Scope: PayrollReviewQueueScope.Open),
                selectedAdminCaseKey: null,
                cancellationToken);
            return shell.SelectedKey
                ?? (shell.Cases.Count > 0 ? shell.Cases[0].AdminCaseKey : null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not resolve next Project300 focus after action {ActionId}.", ActionId);
            return null;
        }
    }

    private async Task<string?> ResolveNextProject200FocusAsync(
        int? year,
        int? month,
        CancellationToken cancellationToken)
    {
        if (year is null or <= 0 || month is null or <= 0)
        {
            return null;
        }

        try
        {
            var shell = await project200WorkbenchService.GetShellAsync(
                year.Value,
                month.Value,
                new PayrollReviewQueueFilter(
                    PayrollReviewCategory.Project200,
                    Scope: PayrollReviewQueueScope.Open),
                selectedAdminCaseKey: null,
                cancellationToken);
            return shell.SelectedKey
                ?? (shell.Cases.Count > 0 ? shell.Cases[0].AdminCaseKey : null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not resolve next Project200 focus after action {ActionId}.", ActionId);
            return null;
        }
    }

    private async Task<string?> ResolveNextProject100FocusAsync(
        int? year,
        int? month,
        CancellationToken cancellationToken)
    {
        if (year is null or <= 0 || month is null or <= 0)
        {
            return null;
        }

        try
        {
            var shell = await project100WorkbenchService.GetShellAsync(
                year.Value,
                month.Value,
                new PayrollReviewQueueFilter(
                    PayrollReviewCategory.Project100,
                    Scope: PayrollReviewQueueScope.Open),
                selectedAdminCaseKey: null,
                cancellationToken);
            return shell.SelectedKey
                ?? (shell.Cases.Count > 0 ? shell.Cases[0].AdminCaseKey : null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not resolve next Project100 focus after action {ActionId}.", ActionId);
            return null;
        }
    }

    private async Task<string?> ResolveNextStandbyFocusAsync(
        int? year,
        int? month,
        CancellationToken cancellationToken)
    {
        if (year is null or <= 0 || month is null or <= 0)
        {
            return null;
        }

        try
        {
            var shell = await standbyWorkbenchService.GetShellAsync(
                year.Value,
                month.Value,
                new PayrollReviewQueueFilter(
                    PayrollReviewCategory.Standby,
                    Scope: PayrollReviewQueueScope.Open),
                selectedAdminCaseKey: null,
                cancellationToken);
            return shell.SelectedKey
                ?? (shell.Cases.Count > 0 ? shell.Cases[0].AdminCaseKey : null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not resolve next Standby focus after action {ActionId}.", ActionId);
            return null;
        }
    }

    private static bool IsStandbyAction(PayrollActionConfirmationView? confirmation)
    {
        if (confirmation is null)
        {
            return false;
        }

        var findingKey = confirmation.Evidence.FindingKey ?? string.Empty;
        if (findingKey.StartsWith("standby-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return confirmation.Evidence.FindingType is PayrollFindingType.StandbyStartMismatch
            or PayrollFindingType.StandbyEndMismatch
            or PayrollFindingType.StandbyDurationMismatch
            or PayrollFindingType.StandbyPhoneExceeds15Min
            or PayrollFindingType.StandbyPossibleWrongDossier
            or PayrollFindingType.StandbyAmbiguousEvidence
            or PayrollFindingType.StandbyNoGpsData;
    }

    private static bool IsProject100Action(PayrollActionConfirmationView? confirmation)
    {
        if (confirmation is null)
        {
            return false;
        }

        var findingKey = confirmation.Evidence.FindingKey ?? string.Empty;
        if (findingKey.StartsWith("p100-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (confirmation.Evidence.FindingType is PayrollFindingType.Project100TrainingHours
            or PayrollFindingType.Project100TrainingInOvertime
            or PayrollFindingType.Project100ExceedsPlannedDuration)
        {
            return true;
        }

        var projectLabel = confirmation.DeleteProposal?.ProjectLabel;
        return string.Equals(projectLabel, "100", StringComparison.Ordinal);
    }

    private static bool IsProject200Action(PayrollActionConfirmationView? confirmation)
    {
        if (confirmation is null)
        {
            return false;
        }

        var findingKey = confirmation.Evidence.FindingKey ?? string.Empty;
        if (findingKey.StartsWith("p200-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (confirmation.Evidence.FindingType is PayrollFindingType.Project200WithoutPlanning
            or PayrollFindingType.Project200ExceedsPlanning)
        {
            return true;
        }

        var projectLabel = confirmation.DeleteProposal?.ProjectLabel;
        return string.Equals(projectLabel, "200", StringComparison.Ordinal);
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

        var create = confirmation?.CreateProposal;
        if (create is not null)
        {
            return $"Prestatie {create.Start:HH:mm}–{create.End:HH:mm} is aangemaakt in Plenion.";
        }

        var adjust = confirmation?.AdjustProposal;
        if (adjust is not null)
        {
            return $"Prestatie #{adjust.PerformanceId} aangepast naar {adjust.ProposedStart:HH:mm}–{adjust.ProposedEnd:HH:mm}.";
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

        if (detail.Contains("CreatePerformanceEnabled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("create staat uit", StringComparison.OrdinalIgnoreCase))
        {
            return "Aanmaken is momenteel uitgeschakeld.";
        }

        if (detail.Contains("uitgeschakeld", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("DeletePerformanceEnabled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ExecutionEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Uitvoering is momenteel uitgeschakeld.";
        }

        if (detail.Contains("already_exists", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("equivalente", StringComparison.OrdinalIgnoreCase))
        {
            return "Er bestaat al een equivalente prestatie. Er is niets nieuws aangemaakt.";
        }

        if (result.ActionId != Guid.Empty && detail.Contains("create", StringComparison.OrdinalIgnoreCase))
        {
            return detail;
        }

        return string.IsNullOrWhiteSpace(detail)
            ? "De actie kon niet worden uitgevoerd. Er is niets gewijzigd."
            : detail;
    }

    private static string MapException(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("verplicht", StringComparison.OrdinalIgnoreCase))
        {
            return "Reden is verplicht.";
        }

        if (message.Contains("CreatePerformanceEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Aanmaken is momenteel uitgeschakeld.";
        }

        if (message.Contains("uitgeschakeld", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ExecutionEnabled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DeletePerformanceEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Uitvoering is momenteel uitgeschakeld.";
        }

        if (message.Contains("niet gevonden", StringComparison.OrdinalIgnoreCase))
        {
            return "Actie niet gevonden. Open de bevestiging opnieuw via de workbench.";
        }

        return "De actie kon niet worden uitgevoerd. Er is niets gewijzigd.";
    }
}

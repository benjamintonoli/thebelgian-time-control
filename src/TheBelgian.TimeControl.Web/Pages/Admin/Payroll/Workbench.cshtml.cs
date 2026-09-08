using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class WorkbenchModel(
    IPayrollProject300WorkbenchService workbenchService,
    IPayrollReviewQueueService reviewQueueService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<WorkbenchModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "default";
    [BindProperty(SupportsGet = true)] public PayrollReviewQueueScope Scope { get; set; } = PayrollReviewQueueScope.Open;

    [BindProperty] public string AdminCaseKey { get; set; } = string.Empty;
    [BindProperty] public string DecisionCode { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public long PerformanceId { get; set; }
    [BindProperty] public string? NewStart { get; set; }
    [BindProperty] public string? NewEnd { get; set; }
    [BindProperty] public string? CorrectionReason { get; set; }

    public PayrollProject300WorkbenchPage? Workbench { get; private set; }
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

    public async Task<IActionResult> OnPostDecideAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var result = await reviewQueueService.SetAdminDecisionAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
                DecisionCode.Trim(),
                Comment,
                RequireActor().AuditIdentity,
                cancellationToken);

            var nextFilter = new PayrollReviewQueueFilter(
                PayrollReviewCategory.Project300,
                Search,
                Sort: Sort,
                Scope: Scope);
            var refreshed = await workbenchService.GetWorkbenchAsync(
                Year,
                Month,
                nextFilter,
                null,
                cancellationToken);
            string? nextKey = null;
            foreach (var item in refreshed.Cases)
            {
                if (!PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
                {
                    continue;
                }

                if (string.Equals(item.AdminCaseKey, result.AdminCaseKey, StringComparison.Ordinal))
                {
                    continue;
                }

                nextKey = item.AdminCaseKey;
                break;
            }

            nextKey ??= refreshed.Cases.Count > 0 ? refreshed.Cases[0].AdminCaseKey : null;

            return new JsonResult(new
            {
                ok = true,
                adminCaseKey = result.AdminCaseKey,
                decisionCode = result.DecisionCode,
                decisionLabel = result.DecisionLabel,
                statusLabel = PayrollGuidedDecisions.AdminStatusLabel(result.Status),
                hideRow = Scope == PayrollReviewQueueScope.Open
                    && !PayrollReviewCategories.IsUnresolved(result.Status),
                nextKey,
                openCorrection = string.Equals(
                    result.DecisionCode,
                    PayrollGuidedDecisionCodes.P300HoursWrong,
                    StringComparison.Ordinal),
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Workbench decide failed for {AdminCaseKey}", AdminCaseKey);
            return new JsonResult(new { ok = false, error = exception.Message })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    public async Task<IActionResult> OnPostProposeCorrectionAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            if (!TimeOnly.TryParse(NewStart, CultureInfo.InvariantCulture, out var start)
                || !TimeOnly.TryParse(NewEnd, CultureInfo.InvariantCulture, out var end))
            {
                return new JsonResult(new { ok = false, error = "Ongeldige VAN/TOT." })
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                };
            }

            var propose = await workbenchService.ProposeTimeCorrectionAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
                PerformanceId,
                start,
                end,
                CorrectionReason ?? string.Empty,
                RequireActor().AuditIdentity,
                cancellationToken);

            if (!propose.Ok)
            {
                return new JsonResult(new { ok = false, error = propose.Message, blockReason = propose.BlockReason })
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                };
            }

            await reviewQueueService.SetAdminDecisionAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
                PayrollGuidedDecisionCodes.P300HoursWrong,
                CorrectionReason,
                RequireActor().AuditIdentity,
                cancellationToken);

            return new JsonResult(new
            {
                ok = true,
                message = propose.Message,
                actionId = propose.ActionId,
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Workbench propose correction failed.");
            return new JsonResult(new { ok = false, error = exception.Message })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Workbench = await workbenchService.GetWorkbenchAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(
                PayrollReviewCategory.Project300,
                Search,
                Sort: Sort,
                Scope: Scope),
            Focus,
            cancellationToken);
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

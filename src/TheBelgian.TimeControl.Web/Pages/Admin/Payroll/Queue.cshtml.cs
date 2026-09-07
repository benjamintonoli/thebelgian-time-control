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

public sealed class QueueModel(
    IPayrollReviewQueueService reviewQueueService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<QueueModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollReviewCategory Category { get; set; } = PayrollReviewCategory.All;
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollFindingStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "default";
    [BindProperty(SupportsGet = true)] public PayrollReviewQueueScope Scope { get; set; } = PayrollReviewQueueScope.Open;
    [BindProperty(SupportsGet = true)] public bool ListMode { get; set; }

    [BindProperty] public List<string> SelectedAdminCaseKeys { get; set; } = [];
    [BindProperty] public string BulkDecisionCode { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string AdminCaseKey { get; set; } = string.Empty;
    [BindProperty] public string DecisionCode { get; set; } = string.Empty;

    public PayrollAdminQueuePage? Queue { get; private set; }
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

    public async Task<IActionResult> OnPostBulkAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var keys = SelectedAdminCaseKeys
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (keys.Length == 0)
            {
                throw new InvalidOperationException("Selecteer minstens één zichtbare case.");
            }

            if (string.IsNullOrWhiteSpace(BulkDecisionCode))
            {
                throw new InvalidOperationException("Kies een bulk-beslissing.");
            }

            var result = await reviewQueueService.BulkSetAdminDecisionAsync(
                Year,
                Month,
                keys,
                BulkDecisionCode,
                Comment ?? string.Empty,
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = $"{result.Updated} controles bijgewerkt.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Bulk guided payroll update failed.");
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new
        {
            year = Year,
            month = Month,
            Category,
            Search,
            Sort,
            Scope,
            ListMode = true,
        });
    }

    public async Task<IActionResult> OnPostDecideAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            if (string.IsNullOrWhiteSpace(AdminCaseKey) || string.IsNullOrWhiteSpace(DecisionCode))
            {
                return new JsonResult(new { ok = false, error = "Ontbrekende case of beslissing." })
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                };
            }

            var result = await reviewQueueService.SetAdminDecisionAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
                DecisionCode.Trim(),
                Comment,
                RequireActor().AuditIdentity,
                cancellationToken);

            var hideRow = Scope == PayrollReviewQueueScope.Open
                && !PayrollReviewCategories.IsUnresolved(result.Status);

            return new JsonResult(new
            {
                ok = true,
                adminCaseKey = result.AdminCaseKey,
                decisionCode = result.DecisionCode,
                decisionLabel = result.DecisionLabel,
                status = result.Status.ToString(),
                statusLabel = PayrollGuidedDecisions.AdminStatusLabel(result.Status),
                findingsUpdated = result.FindingsUpdated,
                hideRow,
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Inline triage decision failed for {AdminCaseKey}.", AdminCaseKey);
            return new JsonResult(new { ok = false, error = exception.Message })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Queue = await reviewQueueService.GetAdminQueueAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(Category, Search, Status, Sort: Sort, Scope: Scope),
            cancellationToken);
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

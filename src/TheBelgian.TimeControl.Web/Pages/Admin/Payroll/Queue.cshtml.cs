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
    IPayrollActionService payrollActionService,
    IOptions<PayrollActionsOptions> actionsOptions,
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
    [BindProperty] public long PerformanceId { get; set; }
    [BindProperty] public string? DeleteReason { get; set; }

    public PayrollAdminQueuePage? Queue { get; private set; }
    public bool ActionsEnabled => actionsOptions.Value.Enabled;
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

    public async Task<IActionResult> OnPostProposeDeleteAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled() || !actionsOptions.Value.Enabled)
        {
            return NotFound();
        }

        try
        {
            await LoadAsync(cancellationToken);
            var adminCase = Queue?.AdminCases.FirstOrDefault(item =>
                string.Equals(item.AdminCaseKey, AdminCaseKey.Trim(), StringComparison.Ordinal));
            if (adminCase is null)
            {
                throw new InvalidOperationException("Admin-case niet gevonden.");
            }

            if (adminCase.Category != PayrollReviewCategory.Project200)
            {
                throw new InvalidOperationException("Delete-voorstel via queue is enkel voor Project 200.");
            }

            if (PerformanceId <= 0)
            {
                throw new InvalidOperationException("PerformanceId is verplicht.");
            }

            var findingType = adminCase.UnderlyingCases
                .SelectMany(item => item.FindingTypes)
                .FirstOrDefault(item => item is PayrollFindingType.Project200WithoutPlanning
                    or PayrollFindingType.Project200ExceedsPlanning);
            if (findingType == default)
            {
                findingType = PayrollFindingType.Project200WithoutPlanning;
            }

            var propose = await payrollActionService.ProposeDeleteForPerformanceAsync(
                Year,
                Month,
                adminCase.ResourceId,
                adminCase.Date,
                PerformanceId,
                DeleteReason ?? Comment ?? "Project 200 prestatie verwijderen",
                RequireActor().AuditIdentity,
                findingType,
                actionKey: $"p200-delete:{adminCase.ResourceId}:{adminCase.Date:yyyyMMdd}:{PerformanceId}",
                sourceFindingKey: adminCase.FindingKeys.Count > 0 ? adminCase.FindingKeys[0] : null,
                sourceFindingId: adminCase.FindingIds.Count > 0 ? adminCase.FindingIds[0] : null,
                sourceFindingKeys: adminCase.FindingKeys.ToArray(),
                sourceFindingIds: adminCase.FindingIds.ToArray(),
                cancellationToken: cancellationToken);

            if (!propose.Ok || propose.ActionId is null)
            {
                Error = propose.Message;
                return Page();
            }

            return RedirectToPage("./ActionConfirm", new { actionId = propose.ActionId });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Queue propose delete failed for {AdminCaseKey}.", AdminCaseKey);
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
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

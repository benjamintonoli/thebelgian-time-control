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

public sealed class MissingTechnicianWorkbenchModel(
    IPayrollIntelligenceWorkbenchService workbenchService,
    IPayrollReviewQueueService reviewQueueService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<MissingTechnicianWorkbenchModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollReviewCategory Category { get; set; } =
        PayrollReviewCategory.MissingPerformance;
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "default";
    [BindProperty(SupportsGet = true)] public PayrollReviewQueueScope Scope { get; set; } = PayrollReviewQueueScope.Open;

    [BindProperty] public string AdminCaseKey { get; set; } = string.Empty;
    [BindProperty] public string DecisionCode { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string? NewStart { get; set; }
    [BindProperty] public string? NewEnd { get; set; }
    [BindProperty] public string? CorrectionReason { get; set; }

    public PayrollIntelligenceWorkbenchPage? Workbench { get; private set; }
    public IReadOnlyDictionary<string, PayrollIntelligenceGpsCacheHint> GpsHints { get; private set; } =
        new Dictionary<string, PayrollIntelligenceGpsCacheHint>(StringComparer.Ordinal);
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    [TempData]
    public string? FlashSuccess { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        NormalizeCategory();

        if (!string.IsNullOrWhiteSpace(FlashSuccess))
        {
            Message = FlashSuccess;
        }

        await LoadShellAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnGetDetailAsync(string? adminCaseKey, CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        NormalizeCategory();
        Workbench = await workbenchService.GetCoreDetailAsync(
            Year,
            Month,
            Category,
            BuildFilter(),
            string.IsNullOrWhiteSpace(adminCaseKey) ? Focus : adminCaseKey,
            cancellationToken);
        GpsHints = BuildHints(Workbench);
        return Partial("_MissingTechnicianDetail", this);
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
            workbenchService.InvalidateQueueCache(Year, Month);

            var refreshed = await workbenchService.GetShellAsync(
                Year,
                Month,
                Category,
                BuildFilter(),
                null,
                cancellationToken);
            string? nextKey = null;
            foreach (var item in refreshed.Cases)
            {
                if (!PayrollReviewCategories.IsUnresolved(item.WorkflowStatus)
                    || string.Equals(item.AdminCaseKey, result.AdminCaseKey, StringComparison.Ordinal))
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
                openCreate = string.Equals(
                    result.DecisionCode,
                    PayrollGuidedDecisionCodes.MissingTechConfirmed,
                    StringComparison.Ordinal),
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MissingTechnician decide failed for {AdminCaseKey}", AdminCaseKey);
            return new JsonResult(new { ok = false, error = exception.Message })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    public async Task<IActionResult> OnPostProposeCreateAsync(CancellationToken cancellationToken)
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

            var propose = await workbenchService.ProposeCreateAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
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

            // Record human decision after a successful Ready proposal so workflow metadata
            // cannot race ahead of action creation. Decision itself is non-material for stale checks.
            await reviewQueueService.SetAdminDecisionAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
                PayrollGuidedDecisionCodes.MissingTechConfirmed,
                CorrectionReason,
                RequireActor().AuditIdentity,
                cancellationToken);

            return new JsonResult(new { ok = true, message = propose.Message, actionId = propose.ActionId });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MissingTechnician propose create failed.");
            return new JsonResult(new { ok = false, error = exception.Message })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    public async Task<IActionResult> OnGetGpsAsync(string adminCaseKey, CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var result = await workbenchService.GetGpsContextAsync(
                Year,
                Month,
                Category,
                adminCaseKey,
                cancellationToken);
            return new JsonResult(new
            {
                ok = true,
                adminCaseKey = result.AdminCaseKey,
                available = result.GpsContext.Available,
                isLoading = result.GpsContext.IsLoading,
                summary = result.GpsContext.Summary,
                cacheHit = result.CacheHit,
                travelModeDutch = result.TravelModeDutch,
                gpsSiteSummary = result.GpsSiteSummary,
                events = result.GpsContext.Events.Select(item => new
                {
                    at = item.At.ToString("HH:mm", CultureInfo.InvariantCulture),
                    end = item.End?.ToString("HH:mm", CultureInfo.InvariantCulture),
                    label = item.Label,
                    detail = item.Detail,
                }),
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MissingTechnician GPS load failed for {AdminCaseKey}", adminCaseKey);
            return new JsonResult(new
            {
                ok = true,
                available = false,
                summary = PayrollIntelligenceWorkbenchBuilder.MissingGpsSummary,
                events = Array.Empty<object>(),
            });
        }
    }

    private async Task LoadShellAsync(CancellationToken cancellationToken)
    {
        NormalizeCategory();
        // Fast first paint: admin-case list from cache/SQLite only. Selected detail loads via
        // Detail handler (persisted evidence; no GPS/PowerFleet N+1 on shell).
        Workbench = await workbenchService.GetShellAsync(
            Year,
            Month,
            Category,
            BuildFilter(),
            Focus,
            cancellationToken);
        GpsHints = BuildHints(Workbench);
    }

    private void NormalizeCategory()
    {
        if (Category is not (PayrollReviewCategory.MissingPerformance or PayrollReviewCategory.WrongDossier))
        {
            Category = PayrollReviewCategory.MissingPerformance;
        }
    }

    private PayrollReviewQueueFilter BuildFilter() =>
        new(Category, Search, Sort: Sort, Scope: Scope);

    private Dictionary<string, PayrollIntelligenceGpsCacheHint> BuildHints(
        PayrollIntelligenceWorkbenchPage? page)
    {
        var hints = new Dictionary<string, PayrollIntelligenceGpsCacheHint>(StringComparer.Ordinal);
        if (page is null)
        {
            return hints;
        }

        foreach (var item in page.Cases)
        {
            hints[item.AdminCaseKey] = workbenchService.GetGpsCacheHint(item.ResourceId, item.Date);
        }

        return hints;
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

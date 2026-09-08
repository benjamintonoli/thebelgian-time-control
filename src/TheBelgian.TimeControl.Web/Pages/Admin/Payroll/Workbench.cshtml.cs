using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;
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
    IOptions<PayrollWorkbenchOptions> workbenchOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    IHostEnvironment hostEnvironment,
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
    [BindProperty] public string? DeleteReason { get; set; }

    public PayrollProject300WorkbenchPage? Workbench { get; private set; }
    public IReadOnlyDictionary<string, PayrollProject300GpsCacheHint> GpsHints { get; private set; } =
        new Dictionary<string, PayrollProject300GpsCacheHint>(StringComparer.Ordinal);
    public bool ShowDiagnostics =>
        workbenchOptions.Value.ShowDiagnostics || hostEnvironment.IsDevelopment();
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

        Workbench = await workbenchService.GetCoreDetailAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(
                PayrollReviewCategory.Project300,
                Search,
                Sort: Sort,
                Scope: Scope),
            string.IsNullOrWhiteSpace(adminCaseKey) ? Focus : adminCaseKey,
            cancellationToken);

        var hints = new Dictionary<string, PayrollProject300GpsCacheHint>(StringComparer.Ordinal);
        if (Workbench is not null)
        {
            foreach (var item in Workbench.Cases)
            {
                hints[item.AdminCaseKey] = workbenchService.GetGpsCacheHint(item.ResourceId, item.Date);
            }
        }

        GpsHints = hints;
        return Partial("_Project300Detail", this);
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

    public async Task<IActionResult> OnPostProposeDeleteAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var propose = await workbenchService.ProposeDeletePerformanceAsync(
                Year,
                Month,
                AdminCaseKey.Trim(),
                PerformanceId,
                DeleteReason ?? CorrectionReason ?? string.Empty,
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
                DeleteReason ?? CorrectionReason,
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
            logger.LogWarning(exception, "Workbench propose delete failed.");
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
                adminCaseKey,
                prefetchNext: true,
                cancellationToken);
            return new JsonResult(new
            {
                ok = true,
                adminCaseKey = result.AdminCaseKey,
                available = result.GpsContext.Available,
                isLoading = result.GpsContext.IsLoading,
                summary = result.GpsContext.Summary,
                neverValidates = PayrollProject300CaseDetail.GpsNeverValidatesNote,
                cacheHit = result.CacheHit,
                events = result.GpsContext.Events.Select(item => new
                {
                    at = item.At.ToString("HH:mm", CultureInfo.InvariantCulture),
                    end = item.End?.ToString("HH:mm", CultureInfo.InvariantCulture),
                    label = item.Label,
                    detail = item.Detail,
                    phase = item.Phase,
                }),
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "GPS load failed for {AdminCaseKey}", adminCaseKey);
            return new JsonResult(new
            {
                ok = true,
                available = false,
                summary = PayrollProject300WorkbenchBuilder.MissingGpsSummary,
                neverValidates = PayrollProject300CaseDetail.GpsNeverValidatesNote,
                events = Array.Empty<object>(),
            });
        }
    }

    private async Task LoadShellAsync(CancellationToken cancellationToken)
    {
        Workbench = await workbenchService.GetShellAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(
                PayrollReviewCategory.Project300,
                Search,
                Sort: Sort,
                Scope: Scope),
            Focus,
            cancellationToken);

        var hints = new Dictionary<string, PayrollProject300GpsCacheHint>(StringComparer.Ordinal);
        if (Workbench is not null)
        {
            foreach (var item in Workbench.Cases)
            {
                hints[item.AdminCaseKey] = workbenchService.GetGpsCacheHint(item.ResourceId, item.Date);
            }
        }

        GpsHints = hints;
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        await LoadShellAsync(cancellationToken);

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

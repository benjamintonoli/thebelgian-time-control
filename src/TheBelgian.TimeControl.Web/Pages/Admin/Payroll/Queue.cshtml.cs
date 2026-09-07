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
    [BindProperty(SupportsGet = true)] public PayrollFindingSeverity? Severity { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollReviewCaseActionability? Actionability { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "default";
    [BindProperty(SupportsGet = true)] public PayrollReviewQueueScope Scope { get; set; } = PayrollReviewQueueScope.Open;
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }

    [BindProperty] public string CaseKey { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string? ReasonChoice { get; set; }
    [BindProperty] public List<string> SelectedCaseKeys { get; set; } = [];
    [BindProperty] public string BulkStatus { get; set; } = string.Empty;

    public PayrollReviewQueuePage? Queue { get; private set; }
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

    public async Task<IActionResult> OnPostFollowUpAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.NeedsFollowUp, goNext: false, requireReason: false, cancellationToken);

    public async Task<IActionResult> OnPostReviewedAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.Reviewed, goNext: false, requireReason: true, cancellationToken);

    public async Task<IActionResult> OnPostDismissedAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.Dismissed, goNext: false, requireReason: true, cancellationToken);

    public async Task<IActionResult> OnPostReviewedAndNextAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.Reviewed, goNext: true, requireReason: true, cancellationToken);

    public async Task<IActionResult> OnPostFollowUpAndNextAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.NeedsFollowUp, goNext: true, requireReason: false, cancellationToken);

    public async Task<IActionResult> OnPostNextAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return RedirectToFilter(FindAdjacentCaseKey(CaseKey, forward: true));
    }

    public async Task<IActionResult> OnPostPreviousAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return RedirectToFilter(FindAdjacentCaseKey(CaseKey, forward: false));
    }

    public async Task<IActionResult> OnPostBulkAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var status = BulkStatus switch
            {
                "followup" => PayrollFindingStatus.NeedsFollowUp,
                "reviewed" => PayrollFindingStatus.Reviewed,
                "dismissed" => PayrollFindingStatus.Dismissed,
                _ => throw new InvalidOperationException("Kies een bulk-actie."),
            };

            // Only explicitly posted keys — never all month keys.
            var keys = SelectedCaseKeys
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (keys.Length == 0)
            {
                throw new InvalidOperationException("Selecteer minstens één zichtbare case.");
            }

            var comment = ResolveComment(requireReason: status is PayrollFindingStatus.Reviewed or PayrollFindingStatus.Dismissed);
            var result = await reviewQueueService.BulkSetCaseStatusAsync(
                Year,
                Month,
                keys,
                status,
                comment ?? string.Empty,
                RequireActor().AuditIdentity,
                cancellationToken);

            var label = PayrollReviewCategories.WorkflowStatusLabel(status);
            Message = $"{result.Updated} controles worden als {label} gemarkeerd.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Bulk payroll review update failed.");
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToFilter(focus: null);
    }

    private async Task<IActionResult> SetStatusAsync(
        PayrollFindingStatus status,
        bool goNext,
        bool requireReason,
        CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var comment = ResolveComment(requireReason);
            await reviewQueueService.SetCaseStatusAsync(
                Year,
                Month,
                CaseKey,
                status,
                comment,
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = status switch
            {
                PayrollFindingStatus.Reviewed => "Gemarkeerd als gecontroleerd (geen correctie nodig).",
                PayrollFindingStatus.NeedsFollowUp => "Gemarkeerd als opvolging nodig.",
                PayrollFindingStatus.Dismissed => "Gemarkeerd als niet van toepassing.",
                _ => "Status bijgewerkt.",
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll review case status update failed for {CaseKey}.", CaseKey);
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        if (goNext)
        {
            await LoadAsync(cancellationToken);
            return RedirectToFilter(FindAdjacentCaseKey(CaseKey, forward: true));
        }

        return RedirectToFilter(CaseKey);
    }

    private string? ResolveComment(bool requireReason)
    {
        var choice = ReasonChoice?.Trim();
        var free = Comment?.Trim();
        string? combined;
        if (!string.IsNullOrWhiteSpace(choice) && !string.Equals(choice, "Andere reden", StringComparison.OrdinalIgnoreCase))
        {
            combined = string.IsNullOrWhiteSpace(free) ? choice : $"{choice} — {free}";
        }
        else
        {
            combined = free;
        }

        if (requireReason && string.IsNullOrWhiteSpace(combined))
        {
            throw new InvalidOperationException("Een reden/commentaar is verplicht.");
        }

        return combined;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Queue = await reviewQueueService.GetQueueAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(Category, Search, Status, Severity, Actionability, Sort, Scope),
            cancellationToken);
    }

    private string? FindAdjacentCaseKey(string currentCaseKey, bool forward)
    {
        if (Queue is null || Queue.Cases.Count == 0)
        {
            return null;
        }

        var list = Queue.Cases.ToList();
        var index = list.FindIndex(item => string.Equals(item.CaseKey, currentCaseKey, StringComparison.Ordinal));
        if (index < 0)
        {
            return list[0].CaseKey;
        }

        if (forward)
        {
            return index + 1 < list.Count ? list[index + 1].CaseKey : list[0].CaseKey;
        }

        return index - 1 >= 0 ? list[index - 1].CaseKey : list[^1].CaseKey;
    }

    private RedirectToPageResult RedirectToFilter(string? focus) =>
        RedirectToPage(new
        {
            year = Year,
            month = Month,
            Category,
            Search,
            Status,
            Severity,
            Actionability,
            Sort,
            Scope,
            Focus = focus,
        });

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

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
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }

    [BindProperty] public string CaseKey { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }

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

    public async Task<IActionResult> OnPostReviewedAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.Reviewed, goNext: false, cancellationToken);

    public async Task<IActionResult> OnPostFollowUpAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.NeedsFollowUp, goNext: false, cancellationToken);

    public async Task<IActionResult> OnPostNextAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        var next = FindNextCaseKey(CaseKey);
        return RedirectToPage(new
        {
            year = Year,
            month = Month,
            Category,
            Search,
            Status,
            Severity,
            Actionability,
            Sort,
            Focus = next,
        });
    }

    public async Task<IActionResult> OnPostReviewedAndNextAsync(CancellationToken cancellationToken) =>
        await SetStatusAsync(PayrollFindingStatus.Reviewed, goNext: true, cancellationToken);

    private async Task<IActionResult> SetStatusAsync(
        PayrollFindingStatus status,
        bool goNext,
        CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await reviewQueueService.SetCaseStatusAsync(
                Year,
                Month,
                CaseKey,
                status,
                Comment,
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = status == PayrollFindingStatus.Reviewed
                ? "Gemarkeerd als gecontroleerd (geen correctie nodig)."
                : "Gemarkeerd als opvolging nodig.";
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
            var next = FindNextCaseKey(CaseKey);
            return RedirectToPage(new
            {
                year = Year,
                month = Month,
                Category,
                Search,
                Status,
                Severity,
                Actionability,
                Sort,
                Focus = next,
            });
        }

        return RedirectToPage(new
        {
            year = Year,
            month = Month,
            Category,
            Search,
            Status,
            Severity,
            Actionability,
            Sort,
            Focus = CaseKey,
        });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Queue = await reviewQueueService.GetQueueAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(Category, Search, Status, Severity, Actionability, Sort),
            cancellationToken);
    }

    private string? FindNextCaseKey(string currentCaseKey)
    {
        if (Queue is null || Queue.Cases.Count == 0)
        {
            return null;
        }

        var open = Queue.Cases
            .Where(item => item.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp)
            .ToList();
        if (open.Count == 0)
        {
            return Queue.Cases.Count > 0 ? Queue.Cases[0].CaseKey : null;
        }

        var index = open.FindIndex(item => string.Equals(item.CaseKey, currentCaseKey, StringComparison.Ordinal));
        if (index < 0)
        {
            return open[0].CaseKey;
        }

        return open[(index + 1) % open.Count].CaseKey;
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

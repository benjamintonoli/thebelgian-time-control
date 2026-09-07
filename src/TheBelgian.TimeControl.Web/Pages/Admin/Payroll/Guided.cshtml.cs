using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class GuidedModel(
    IPayrollReviewQueueService reviewQueueService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<GuidedModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollReviewCategory Category { get; set; } = PayrollReviewCategory.Project300;
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public PayrollReviewQueueScope Scope { get; set; } = PayrollReviewQueueScope.Open;
    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }

    [BindProperty] public string AdminCaseKey { get; set; } = string.Empty;
    [BindProperty] public string DecisionCode { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string? PhoneConfirmed { get; set; }

    public PayrollAdminQueuePage? Queue { get; private set; }
    public PayrollAdminCase? Current { get; private set; }
    public int CurrentIndex { get; private set; }
    public int TotalVisible { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled() || Category == PayrollReviewCategory.All)
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDecideAsync(CancellationToken cancellationToken) =>
        await DecideAsync(goNext: false, cancellationToken);

    public async Task<IActionResult> OnPostDecideAndNextAsync(CancellationToken cancellationToken) =>
        await DecideAsync(goNext: true, cancellationToken);

    public async Task<IActionResult> OnPostNextAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return RedirectToFocus(FindAdjacent(AdminCaseKey, forward: true));
    }

    public async Task<IActionResult> OnPostPreviousAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return RedirectToFocus(FindAdjacent(AdminCaseKey, forward: false));
    }

    private async Task<IActionResult> DecideAsync(bool goNext, CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var comment = Comment;
            if (!string.IsNullOrWhiteSpace(PhoneConfirmed)
                && string.Equals(DecisionCode, PayrollGuidedDecisionCodes.StandbyPhoneThenPhysical, StringComparison.Ordinal))
            {
                var phoneNote = PhoneConfirmed switch
                {
                    "yes" => "Telefonisch contact bevestigd: ja",
                    "no" => "Telefonisch contact bevestigd: nee",
                    _ => "Telefonisch contact bevestigd: onbekend",
                };
                comment = string.IsNullOrWhiteSpace(comment) ? phoneNote : $"{phoneNote} — {comment}";
            }

            var result = await reviewQueueService.SetAdminDecisionAsync(
                Year,
                Month,
                AdminCaseKey,
                DecisionCode,
                comment,
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = $"{result.DecisionLabel} opgeslagen.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Guided payroll decision failed for {AdminCaseKey}.", AdminCaseKey);
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        if (goNext)
        {
            await LoadAsync(cancellationToken);
            return RedirectToFocus(FindAdjacent(AdminCaseKey, forward: true));
        }

        return RedirectToFocus(AdminCaseKey);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Queue = await reviewQueueService.GetAdminQueueAsync(
            Year,
            Month,
            new PayrollReviewQueueFilter(Category, Search, Scope: Scope, Sort: "default"),
            cancellationToken);
        TotalVisible = Queue.AdminCases.Count;
        if (TotalVisible == 0)
        {
            Current = null;
            CurrentIndex = 0;
            return;
        }

        var index = 0;
        if (!string.IsNullOrWhiteSpace(Focus))
        {
            index = Queue.AdminCases.ToList().FindIndex(item =>
                string.Equals(item.AdminCaseKey, Focus, StringComparison.Ordinal));
            if (index < 0)
            {
                index = 0;
            }
        }

        CurrentIndex = index;
        Current = Queue.AdminCases[index];
    }

    private string? FindAdjacent(string currentKey, bool forward)
    {
        if (Queue is null || Queue.AdminCases.Count == 0)
        {
            return null;
        }

        var list = Queue.AdminCases.ToList();
        var index = list.FindIndex(item => string.Equals(item.AdminCaseKey, currentKey, StringComparison.Ordinal));
        if (index < 0)
        {
            return list[0].AdminCaseKey;
        }

        if (forward)
        {
            return index + 1 < list.Count ? list[index + 1].AdminCaseKey : null;
        }

        return index - 1 >= 0 ? list[index - 1].AdminCaseKey : list[^1].AdminCaseKey;
    }

    private RedirectToPageResult RedirectToFocus(string? focus)
    {
        if (focus is null)
        {
            return RedirectToPage("./Queue", new { year = Year, month = Month, Category, Scope });
        }

        return RedirectToPage(new
        {
            year = Year,
            month = Month,
            Category,
            Search,
            Scope,
            Focus = focus,
        });
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}

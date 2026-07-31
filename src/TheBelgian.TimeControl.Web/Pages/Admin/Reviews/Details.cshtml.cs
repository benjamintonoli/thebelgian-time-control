using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Reviews;

public sealed class DetailsModel(
    IAdminReviewService reviewService,
    ILogger<DetailsModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long PerformanceId { get; set; }

    [BindProperty]
    public AdminReviewStatus Decision { get; set; } = AdminReviewStatus.Confirmed;

    [BindProperty]
    public string Reviewer { get; set; } = string.Empty;

    [BindProperty]
    public string? Comment { get; set; }

    [BindProperty]
    public string? ChosenVisitCandidateId { get; set; }

    public ReviewCase? Case { get; private set; }

    public IReadOnlyList<AdminReviewDecisionAudit> AuditTrail { get; private set; } = [];

    public string? Message { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (Case is null)
        {
            return NotFound();
        }

        ChosenVisitCandidateId = Case.Admin.ChosenVisitCandidateId
            ?? Case.Matcher.ProposedVisit?.VisitCandidateId;
        Reviewer = Case.Admin.Reviewer ?? string.Empty;
        Comment = Case.Admin.Comment;
        if (Case.ReviewStatus != AdminReviewStatus.Pending)
        {
            Decision = Case.ReviewStatus;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string>? stopIds = null;
            await LoadAsync(cancellationToken);
            if (Case is not null && !string.IsNullOrWhiteSpace(ChosenVisitCandidateId))
            {
                stopIds = Case.Matcher.CandidateVisits
                    .FirstOrDefault(item =>
                        string.Equals(
                            item.VisitCandidateId,
                            ChosenVisitCandidateId,
                            StringComparison.Ordinal))
                    ?.ConstituentStopIds;
            }

            await reviewService.RecordDecisionAsync(
                PerformanceId,
                Decision,
                Reviewer,
                Comment,
                ChosenVisitCandidateId,
                stopIds,
                cancellationToken);
            Message = "Beslissing opgeslagen (append-only audit). Geen Plenion-writeback.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin review decision failed for {PerformanceId}", PerformanceId);
            Error = ex.Message;
        }

        await LoadAsync(cancellationToken);
        if (Case is null)
        {
            return NotFound();
        }

        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Case = await reviewService.GetAsync(PerformanceId, cancellationToken);
        AuditTrail = await reviewService.GetAuditTrailAsync(PerformanceId, cancellationToken);
    }
}

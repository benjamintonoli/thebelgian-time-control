using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Reviews;

public sealed class DetailsModel(
    IAdminReviewService reviewService,
    ICurrentUserContext currentUser,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<DetailsModel> logger) : PageModel
{
    public enum ReviewActionKind
    {
        ConfirmProposal = 0,
        ChooseOtherCandidate = 1,
        RejectProposal = 2,
        NoReliableMatch = 3,
        NeedsMoreInformation = 4,
    }

    [BindProperty(SupportsGet = true)]
    public long PerformanceId { get; set; }

    [BindProperty]
    public ReviewActionKind Action { get; set; } = ReviewActionKind.ConfirmProposal;

    [BindProperty]
    public string? Comment { get; set; }

    [BindProperty]
    public string? ChosenVisitCandidateId { get; set; }

    public ReviewCase? Case { get; private set; }

    public IReadOnlyList<AdminReviewDecisionAudit> AuditTrail { get; private set; } = [];

    public IReadOnlyList<AdminReviewSessionMetric> SessionMetrics { get; private set; } = [];

    public bool IsLivePilot { get; private set; }

    public string? Message { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (Case is null)
        {
            return NotFound();
        }

        await reviewService.RecordCaseOpenedAsync(PerformanceId, cancellationToken);
        SessionMetrics = await reviewService.GetSessionMetricsAsync(PerformanceId, cancellationToken);

        ChosenVisitCandidateId = Case.Admin.ChosenVisitCandidateId
            ?? Case.Matcher.ProposedVisit?.VisitCandidateId;
        Comment = Case.Admin.Comment;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
            if (Case is null)
            {
                return NotFound();
            }

            var actor = currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
            var (decision, chosenId) = MapAction(Case);
            IReadOnlyList<string>? stopIds = null;
            if (!string.IsNullOrWhiteSpace(chosenId))
            {
                stopIds = Case.Matcher.CandidateVisits
                    .FirstOrDefault(item =>
                        string.Equals(item.VisitCandidateId, chosenId, StringComparison.Ordinal))
                    ?.ConstituentStopIds;
            }

            await reviewService.RecordDecisionAsync(
                PerformanceId,
                decision,
                actor.AuditIdentity,
                actor.Subject,
                Comment,
                chosenId,
                stopIds,
                cancellationToken);
            Message = "Beslissing opgeslagen in append-only audittrail. Geen Plenion-writeback.";
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

    private (AdminReviewStatus Decision, string? ChosenVisitId) MapAction(ReviewCase current)
    {
        return Action switch
        {
            ReviewActionKind.ConfirmProposal => (
                AdminReviewStatus.Confirmed,
                current.Matcher.ProposedVisit?.VisitCandidateId ?? ChosenVisitCandidateId),
            ReviewActionKind.ChooseOtherCandidate => (
                AdminReviewStatus.Confirmed,
                ChosenVisitCandidateId),
            ReviewActionKind.RejectProposal => (AdminReviewStatus.Rejected, null),
            ReviewActionKind.NoReliableMatch => (AdminReviewStatus.NoReliableMatch, null),
            ReviewActionKind.NeedsMoreInformation => (AdminReviewStatus.NeedsMoreInformation, null),
            _ => throw new InvalidOperationException("Onbekende adminactie."),
        };
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLivePilot = reviewService.IsLivePilot;
        Case = await reviewService.GetAsync(PerformanceId, cancellationToken);
        AuditTrail = await reviewService.GetAuditTrailAsync(PerformanceId, cancellationToken);
        SessionMetrics = await reviewService.GetSessionMetricsAsync(PerformanceId, cancellationToken);
    }
}

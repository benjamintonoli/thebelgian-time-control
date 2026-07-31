using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Reviews;

public sealed class DetailsModel(
    IAdminReviewService reviewService,
    IWebHostEnvironment environment,
    ILogger<DetailsModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long PerformanceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Technician { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public DateOnly Date { get; set; }

    [BindProperty]
    public AdminReviewStatus Decision { get; set; } = AdminReviewStatus.Confirmed;

    [BindProperty]
    public string Reviewer { get; set; } = string.Empty;

    [BindProperty]
    public string? Comment { get; set; }

    [BindProperty]
    public string? ChosenVisitCandidateId { get; set; }

    public AdminReviewCase? Case { get; private set; }
    public IReadOnlyList<AdminReviewDecisionAudit> AuditTrail { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        return await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        try
        {
            await LoadCaseOnlyAsync(cancellationToken);
            IReadOnlyList<string>? chosenStops = null;
            if (!string.IsNullOrWhiteSpace(ChosenVisitCandidateId))
            {
                chosenStops = Case?.CandidateVisits
                    .FirstOrDefault(item => item.VisitCandidateId == ChosenVisitCandidateId)
                    ?.ConstituentStopIds;
            }

            await reviewService.RecordDecisionAsync(
                PerformanceId,
                Technician,
                Date,
                Decision,
                Reviewer,
                Comment,
                string.IsNullOrWhiteSpace(ChosenVisitCandidateId) ? null : ChosenVisitCandidateId,
                chosenStops,
                cancellationToken);
            Message = "Beslissing opgeslagen (append-only audit, geen Plenion-writeback).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Admin review decision failed.");
            ErrorMessage = exception.Message;
        }

        return await LoadAsync(cancellationToken);
    }

    private async Task LoadCaseOnlyAsync(CancellationToken cancellationToken)
    {
        Case = await reviewService.GetAsync(PerformanceId, Technician, Date, cancellationToken);
    }

    private async Task<IActionResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Case = await reviewService.GetAsync(PerformanceId, Technician, Date, cancellationToken);
            if (Case is null)
            {
                return NotFound();
            }

            AuditTrail = await reviewService.GetAuditTrailAsync(PerformanceId, cancellationToken);
            if (Case.ReviewStatus != AdminReviewStatus.Pending)
            {
                Decision = Case.ReviewStatus;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Admin review detail load failed.");
            ErrorMessage = exception.Message;
        }

        return Page();
    }
}

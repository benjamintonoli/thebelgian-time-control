using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.TimeControl;

public sealed class IndexModel(
    IMonthlyReviewService monthlyReviewService,
    ICurrentUserContext currentUser,
    TimeProvider timeProvider,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Month { get; set; }
    [BindProperty(SupportsGet = true)] public DailyReviewQueueView View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Technician { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true)] public DailyReviewBoundaryFilter Boundary { get; set; }
    [BindProperty(SupportsGet = true)] public DailyReviewEvidenceLevel? Evidence { get; set; }
    [BindProperty(SupportsGet = true)] public bool EscalatedOnly { get; set; }
    [BindProperty(SupportsGet = true)] public DailyReviewSort Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedCaseId { get; set; }

    [BindProperty] public string CaseId { get; set; } = string.Empty;
    [BindProperty] public ReviewFeedbackReason? Reason { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public TimeOnly? ProposedStartTime { get; set; }
    [BindProperty] public TimeOnly? ProposedEndTime { get; set; }
    [BindProperty] public bool ConfirmOpenCases { get; set; }
    [BindProperty] public long ProposalId { get; set; }
    [BindProperty] public bool ConfirmCorrectionExecution { get; set; }

    public MonthlyReviewCockpit Monthly { get; private set; } = Empty();
    public DailyReviewCockpit Cockpit => Monthly.Review;
    public IReadOnlyList<DailyReviewActionAudit> AuditTrail { get; private set; } = [];
    public string? PreviousCaseId { get; private set; }
    public string? NextCaseId { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public DailyCorrectionProposal? CorrectionProposal { get; private set; }
    public CorrectionExecutionAvailability CorrectionAvailability { get; private set; } =
        new(false, false, "Correcties uitvoeren is uitgeschakeld.");

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostDecisionAsync(
        DailyReviewWorkflowStatus decision,
        CancellationToken cancellationToken)
    {
        var month = ResolveMonth();
        try
        {
            var actor = RequireActor();
            var openBefore = await monthlyReviewService.GetCockpitAsync(
                month, new DailyReviewFilter(DailyReviewQueueView.Open), CaseId, cancellationToken);
            var currentIndex = openBefore.Review.Cases
                .Select((item, index) => new { item.CaseId, Index = index })
                .FirstOrDefault(item => item.CaseId == CaseId)?.Index;
            var cockpit = await monthlyReviewService.GetCockpitAsync(
                month, new DailyReviewFilter(DailyReviewQueueView.All), CaseId, cancellationToken);
            var current = cockpit.Review.Selected
                ?? throw new InvalidOperationException("Reviewcase niet gevonden.");
            await monthlyReviewService.SaveDecisionAsync(month, new SaveDailyReviewDecision(
                CaseId, decision, Reason, actor.AuditIdentity, Notes,
                Combine(current.Date, ProposedStartTime, current.First.PlenionTime.Offset),
                Combine(current.Date, ProposedEndTime, current.Last.PlenionTime.Offset),
                actor.Subject), cancellationToken);
            var resultMessage = decision == DailyReviewWorkflowStatus.PendingCorrection
                ? "Correctievoorstel opgeslagen. Er is niets naar Plenion geschreven."
                : "Beoordeling opgeslagen.";
            if (decision == DailyReviewWorkflowStatus.PendingCorrection)
            {
                View = DailyReviewQueueView.ToReview;
                ClearFilters();
                SelectedCaseId = CaseId;
                Message = resultMessage;
            }
            else
            {
                var openAfter = await monthlyReviewService.GetCockpitAsync(
                    month, new DailyReviewFilter(DailyReviewQueueView.Open), null, cancellationToken);
                View = DailyReviewQueueView.Open;
                ClearFilters();
                SelectedCaseId = DailyReviewDisplay.NextOpenCaseId(
                    openAfter.Review.Cases, currentIndex);
                Message = SelectedCaseId is null
                    ? "Alle reviewcases zijn behandeld."
                    : resultMessage;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Monthly review decision failed for {CaseId}", CaseId);
            Error = exception.Message;
            SelectedCaseId = CaseId;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteCorrectionAsync(CancellationToken cancellationToken)
    {
        SelectedCaseId = CaseId;
        View = DailyReviewQueueView.ToReview;
        try
        {
            if (!ConfirmCorrectionExecution)
                throw new InvalidOperationException("Bevestig de correctie voordat je ze uitvoert.");
            if (Reason is null)
                throw new InvalidOperationException("Selecteer een reden.");

            var actor = RequireActor();
            var month = ResolveMonth();
            var cockpit = await monthlyReviewService.GetCockpitAsync(
                month, new DailyReviewFilter(DailyReviewQueueView.All), CaseId, cancellationToken);
            var current = cockpit.Review.Selected
                ?? throw new InvalidOperationException("Reviewcase niet gevonden.");

            var result = await monthlyReviewService.ExecuteDirectCorrectionAsync(
                month,
                new ExecuteDirectCorrectionRequest(
                    CaseId,
                    Reason.Value,
                    actor.AuditIdentity,
                    Notes,
                    Combine(current.Date, ProposedStartTime, current.First.PlenionTime.Offset),
                    Combine(current.Date, ProposedEndTime, current.Last.PlenionTime.Offset),
                    actor.Subject),
                cancellationToken);
            if (result.Status == CorrectionProposalStatuses.Executed)
            {
                var parts = new List<string> { "✓ Correctie uitgevoerd in Plenion." };
                if (result.Proposal.ProposedStart is not null)
                {
                    parts.Add(
                        $"Start: {result.Proposal.OriginalStart:HH:mm} → {(result.Proposal.ExecutedStart ?? result.Proposal.ProposedStart):HH:mm}");
                }
                else
                {
                    parts.Add("Start: ongewijzigd");
                }

                if (result.Proposal.ProposedEnd is not null)
                {
                    parts.Add(
                        $"Einde: {result.Proposal.OriginalEnd:HH:mm} → {(result.Proposal.ExecutedEnd ?? result.Proposal.ProposedEnd):HH:mm}");
                }
                else
                {
                    parts.Add("Einde: ongewijzigd");
                }

                parts.Add($"Uitgevoerd door: {result.Proposal.ExecutedBy}");
                if (result.Proposal.ExecutedAt is not null)
                    parts.Add($"Uitgevoerd op: {result.Proposal.ExecutedAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}");
                Message = string.Join(" ", parts);
                View = DailyReviewQueueView.Completed;
            }
            else if (result.Status == CorrectionProposalStatuses.Conflict)
            {
                Error =
                    "De registratie werd ondertussen gewijzigd in Plenion. Vernieuw de gegevens en controleer deze case opnieuw.";
                View = DailyReviewQueueView.ToReview;
            }
            else
            {
                Message = result.Message;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Direct correction execution failed for case {CaseId}", CaseId);
            Error = exception.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var actor = RequireActor();
            var result = await monthlyReviewService.PrepareAsync(
                ResolveMonth(), actor.AuditIdentity,
                null, true, cancellationToken);
            Message = $"Gegevens vernieuwd: {result.NewCases} nieuw, {result.ChangedCases} gewijzigd, " +
                      $"{result.UnchangedCases} ongewijzigd.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Monthly review refresh failed.");
            Error = exception.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostFinalizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var actor = RequireActor();
            await monthlyReviewService.FinalizeAsync(
                ResolveMonth(), actor.AuditIdentity, ConfirmOpenCases, cancellationToken);
            Message = "Maand afgesloten. Het definitieve rapport is beschikbaar.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Monthly review finalize failed.");
            Error = exception.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnGetReportAsync(CancellationToken cancellationToken)
    {
        var month = ResolveMonth();
        var html = await monthlyReviewService.BuildHtmlReportAsync(month, cancellationToken);
        return Content(html, "text/html; charset=utf-8");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var month = ResolveMonth();
            Month = month.Key;
            Monthly = await monthlyReviewService.GetCockpitAsync(
                month,
                new DailyReviewFilter(View, Technician, Date, Boundary, Evidence, EscalatedOnly, Sort),
                SelectedCaseId,
                cancellationToken);
            if (Cockpit.Selected is { } selected)
            {
                SelectedCaseId = selected.CaseId;
                CaseId = selected.CaseId;
                AuditTrail = await monthlyReviewService.GetAuditTrailAsync(selected.CaseId, cancellationToken);
                CorrectionProposal = await monthlyReviewService.GetLatestCorrectionProposalAsync(
                    selected.CaseId, cancellationToken);
                CorrectionAvailability = await monthlyReviewService
                    .GetCorrectionExecutionAvailabilityAsync(cancellationToken);
                PreviousCaseId = DailyReviewDisplay.AdjacentCaseId(
                    Cockpit.Cases, selected.CaseId, -1);
                NextCaseId = DailyReviewDisplay.AdjacentCaseId(
                    Cockpit.Cases, selected.CaseId, 1);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Monthly review cockpit failed.");
            Error = exception.Message;
        }
    }

    private ReviewMonth ResolveMonth()
    {
        if (!string.IsNullOrWhiteSpace(Month) &&
            DateOnly.TryParseExact(Month + "-01", "yyyy-MM-dd", out var parsed))
            return new ReviewMonth(parsed.Year, parsed.Month);
        return monthlyReviewService.GetDefaultMonth(timeProvider.GetLocalNow());
    }

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);

    private static DateTimeOffset? Combine(DateOnly date, TimeOnly? time, TimeSpan offset) =>
        time is null ? null : new DateTimeOffset(date.ToDateTime(time.Value), offset);

    private void ClearFilters()
    {
        Technician = null;
        Date = null;
        Boundary = DailyReviewBoundaryFilter.All;
        Evidence = null;
        EscalatedOnly = false;
        Sort = DailyReviewSort.LargestDifference;
    }

    private static MonthlyReviewCockpit Empty()
    {
        var month = new ReviewMonth(2000, 1);
        return new MonthlyReviewCockpit(
            new MonthlyReviewPeriod { Year = 2000, Month = 1 },
            new DailyReviewCockpit([], null, [], new DailyReviewCounts(0, 0, 0, 0)),
            new MonthlyReviewSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
            month,
            month);
    }
}

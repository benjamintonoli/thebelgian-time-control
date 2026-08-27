using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Web.Pages.Admin.TimeControl;

public sealed class VehicleAssignmentsModel(
    HistoricalVehicleCandidateCache candidateCache,
    HistoricalVehicleAssignmentWorkflowService workflowService,
    VehicleAssignmentSyncHistoryService syncHistoryService,
    IOptions<VehicleAssignmentReviewOptions> reviewOptions,
    ILogger<VehicleAssignmentsModel> logger) : PageModel
{
    [BindProperty] public List<string> SelectedCandidateKeys { get; set; } = [];
    [BindProperty] public string CandidateKey { get; set; } = string.Empty;
    [BindProperty] public string TechnicianCode { get; set; } = string.Empty;
    [BindProperty] public string ObjectId { get; set; } = string.Empty;
    [BindProperty] public string? PreviousObjectId { get; set; }
    [BindProperty] public DateOnly From { get; set; } = new(2026, 7, 1);
    [BindProperty] public DateOnly Through { get; set; } = new(2026, 7, 31);
    [BindProperty] public DateOnly? TransferDate { get; set; }
    [BindProperty] public string EvidenceNote { get; set; } = string.Empty;

    public string DefaultReviewer => reviewOptions.Value.DefaultReviewer;
    public HistoricalVehicleCandidateResult? Result { get; private set; }
    public DateTimeOffset? LastSuccessfulVehicleAssignmentSyncAt { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(bool refresh, CancellationToken cancellationToken) =>
        await LoadAsync(refresh, cancellationToken);

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(async () =>
        {
            await workflowService.ConfirmCandidatesAsync(
                [CandidateKey], DefaultReviewer, false, cancellationToken);
            Message = "Historische juli-assignment bevestigd en geaudit.";
        }, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostBulkConfirmAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(async () =>
        {
            var assignments = await workflowService.ConfirmCandidatesAsync(
                SelectedCandidateKeys, DefaultReviewer, true, cancellationToken);
            Message = $"{assignments.Count} vooraf getoonde HighConfidenceCandidates bevestigd.";
        }, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCustomAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(async () =>
        {
            await workflowService.ConfirmCustomAsync(
                TechnicianCode, ObjectId, From, Through, DefaultReviewer,
                $"Admin koos ander voertuig. {EvidenceNote}", cancellationToken);
            candidateCache.MarkConfirmed([CandidateKey]);
            Message = "Afwijkend voertuig expliciet bevestigd; niets stil overschreven.";
        }, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostTransferAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(async () =>
        {
            if (TransferDate is null || string.IsNullOrWhiteSpace(PreviousObjectId))
                throw new ArgumentException("Vorig ObjectId en transferdatum zijn verplicht.");
            await workflowService.RegisterTransferAsync(new HistoricalVehicleTransferRequest(
                TechnicianCode, PreviousObjectId, ObjectId, TransferDate.Value,
                From, Through, DefaultReviewer, EvidenceNote), cancellationToken);
            candidateCache.MarkConfirmed([CandidateKey]);
            Message = "Twee exclusieve transferperioden bevestigd en samen geaudit.";
        }, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostInsufficientAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(async () =>
        {
            await workflowService.RecordInsufficientInformationAsync(
                CandidateKey, TechnicianCode, DefaultReviewer, EvidenceNote, cancellationToken);
            Message = "Onvoldoende informatie geregistreerd; er is geen assignment aangemaakt.";
        }, cancellationToken);
        return Page();
    }

    private async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Historical vehicle initialization action failed.");
            Error = exception.Message;
        }
        await LoadAsync(false, cancellationToken);
    }

    private async Task LoadAsync(bool refresh, CancellationToken cancellationToken)
    {
        try
        {
            LastSuccessfulVehicleAssignmentSyncAt = await syncHistoryService
                .LastSuccessfulVehicleAssignmentSyncAtAsync(cancellationToken);
            Result = await candidateCache.GetAsync(refresh, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Historical vehicle candidates could not be loaded.");
            Error ??= exception.Message;
        }
    }
}

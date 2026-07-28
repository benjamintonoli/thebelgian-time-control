using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Exceptions;

public sealed class IndexModel(
    IExceptionRepository repository,
    ISynchronizationService synchronizationService,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ThroughDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Technician { get; set; }

    [BindProperty(SupportsGet = true)]
    public ExceptionPriority? Priority { get; set; }

    [BindProperty(SupportsGet = true)]
    public ReviewDecision? Status { get; set; }

    public IReadOnlyList<DetectedException> Exceptions { get; private set; } = [];

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Exceptions = await repository.SearchAsync(
            new ExceptionFilter(FromDate, ThroughDate, Technician, Priority, Status),
            cancellationToken);
    }

    public async Task<IActionResult> OnPostSynchronizeAsync(
        DateOnly synchronizationFrom,
        DateOnly synchronizationThrough,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await synchronizationService.SynchronizeAsync(
                synchronizationFrom,
                synchronizationThrough,
                cancellationToken);
            Message =
                $"Synchronisatie voltooid: {result.ImportedPlenionCount} prestaties, " +
                $"{result.ImportedPowerfleetCount} ritten en {result.DetectedExceptionCount} afwijkingen.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Handmatig gestarte synchronisatie is mislukt.");
            Error = exception.Message;
        }

        return RedirectToPage();
    }
}

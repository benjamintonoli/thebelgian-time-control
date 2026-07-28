using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Pilot;

public sealed class IndexModel(
    IReadOnlyPilotService pilotService,
    IWebHostEnvironment environment,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public string TechnicianQuery { get; set; } = string.Empty;

    [BindProperty]
    public DateOnly FromDate { get; set; }

    [BindProperty]
    public DateOnly ThroughDate { get; set; }

    [BindProperty]
    public string PowerfleetDriverId { get; set; } = string.Empty;

    [BindProperty]
    public string PowerfleetObjectId { get; set; } = string.Empty;

    [BindProperty]
    public string VehiclePlate { get; set; } = string.Empty;

    public ReadOnlyPilotResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        TechnicianQuery = string.Empty;
        FromDate = new DateOnly(2026, 7, 22);
        ThroughDate = new DateOnly(2026, 7, 24);
        PowerfleetDriverId = string.Empty;
        PowerfleetObjectId = string.Empty;
        VehiclePlate = string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            Result = await pilotService.RunAsync(
                new ReadOnlyPilotRequest(
                    TechnicianQuery,
                    FromDate,
                    ThroughDate,
                    PowerfleetDriverId,
                    PowerfleetObjectId,
                    VehiclePlate,
                    [
                        new PilotAbsence(
                            new DateOnly(2026, 7, 22),
                            "Verlof",
                            "Bevestigde geldige afwezigheid")
                    ]),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Read-only pilot gestopt met fouttype {ExceptionType}.",
                exception.GetType().Name);
            ErrorMessage = exception.Message;
        }

        return Page();
    }
}

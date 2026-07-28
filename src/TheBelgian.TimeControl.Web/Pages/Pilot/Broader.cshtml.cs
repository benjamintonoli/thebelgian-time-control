using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Pilot;

public sealed class BroaderModel(
    IBroaderValidationPilotService validationService,
    IWebHostEnvironment environment,
    ILogger<BroaderModel> logger) : PageModel
{
    public static readonly string[] DefaultTechnicianNames =
    [
        "Filip Dekuyper",
        "Jonas Deklerck",
        "Jasper De Smet",
        "Jarno Vergauwen",
        "Dimitri Stiers",
    ];

    [BindProperty]
    public List<TechnicianInput> Technicians { get; set; } = CreateDefaults();

    [BindProperty]
    public DateOnly FromDate { get; set; }

    [BindProperty]
    public DateOnly ThroughDate { get; set; }

    [BindProperty]
    public int MaxWorkingDaysPerTechnician { get; set; } = 5;

    public BroaderValidationResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        Technicians = CreateDefaults();
        FromDate = new DateOnly(2026, 7, 1);
        ThroughDate = new DateOnly(2026, 7, 28);
        MaxWorkingDaysPerTechnician = 5;
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
            Result = await validationService.RunAsync(
                new BroaderValidationRequest(
                    Technicians
                        .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                        .Select(item => new BroaderValidationTechnicianRequest(
                            item.Name.Trim(),
                            string.IsNullOrWhiteSpace(item.DriverId)
                                ? null
                                : item.DriverId.Trim()))
                        .ToArray(),
                    FromDate,
                    ThroughDate,
                    MaxWorkingDaysPerTechnician),
                cancellationToken);
            await WriteDiagnosticReportAsync(Result, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Bredere validatie gestopt met fouttype {ExceptionType}.",
                exception.GetType().Name);
            ErrorMessage = exception.Message;
        }

        return Page();
    }

    private async Task WriteDiagnosticReportAsync(
        BroaderValidationResult result,
        CancellationToken cancellationToken)
    {
        var docsPath = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, "..", "..", "docs"));
        Directory.CreateDirectory(docsPath);
        var markdownPath = Path.Combine(docsPath, "broader-validation-report.md");
        var jsonPath = Path.Combine(docsPath, "broader-validation-report.json");
        await System.IO.File.WriteAllTextAsync(
            markdownPath,
            BroaderValidationReportWriter.ToMarkdown(result),
            Encoding.UTF8,
            cancellationToken);
        await System.IO.File.WriteAllTextAsync(
            jsonPath,
            BroaderValidationReportWriter.ToJson(result),
            Encoding.UTF8,
            cancellationToken);
        logger.LogInformation(
            "Diagnostisch rapport geschreven naar {MarkdownPath}",
            markdownPath);
    }

    private static List<TechnicianInput> CreateDefaults() =>
        DefaultTechnicianNames
            .Select(name => new TechnicianInput { Name = name })
            .ToList();

    public sealed class TechnicianInput
    {
        public string Name { get; set; } = string.Empty;
        public string DriverId { get; set; } = string.Empty;
    }
}

internal static class BroaderValidationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToMarkdown(BroaderValidationResult result)
    {
        var summary = result.Summary;
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "# Bredere read-only validatie",
            string.Empty,
            string.Create(
                culture,
                $"Periode: {result.FromDate:dd/MM/yyyy} – {result.ThroughDate:dd/MM/yyyy}"),
            string.Empty,
            "## Samenvatting",
            string.Empty,
            string.Create(culture, $"- Verwerkte techniekers: {summary.ProcessedTechnicianCount}"),
            string.Create(culture, $"- Werkdagen: {summary.WorkdayCount}"),
            string.Create(culture, $"- Prestaties: {summary.TotalPerformanceCount}"),
            string.Create(
                culture,
                $"- Automatisch bevestigd: {summary.ConfirmedPercent}% ({summary.ConfirmedLocationMatchCount})"),
            string.Create(
                culture,
                $"- Waarschijnlijk gekoppeld: {summary.ProbablePercent}% ({summary.ProbableLocationMatchCount})"),
            string.Create(
                culture,
                $"- Manuele controle: {summary.ManualReviewPercent}% ({summary.ManualReviewRequiredCount})"),
            string.Create(
                culture,
                $"- Betrouwbare matches (confirmed+probable): {summary.ReliableMatchPercent}%"),
            string.Create(
                culture,
                $"- Ritten zonder bestuurder (MissingDriver): {summary.MissingDriverTripCount}"),
            string.Create(
                culture,
                $"- Mogelijke urenafwijkingen (>3 min): {summary.PossibleHourDeviationCount}"),
            string.Create(
                culture,
                $"- Boven individuele tolerantie (15 min): {summary.IndividualToleranceDeviationCount}"),
            string.Create(
                culture,
                $"- Boven hoge prioriteit (30 min): {summary.HighPriorityToleranceDeviationCount}"),
            string.Empty,
            "### Terugkerende adresproblemen",
            string.Empty,
        };

        if (summary.RecurringAddressProblems.Count == 0)
        {
            lines.Add("- Geen terugkerende adresproblemen geregistreerd.");
        }
        else
        {
            lines.AddRange(summary.RecurringAddressProblems.Select(problem => "- " + problem));
        }

        if (summary.SkippedTechnicians.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("### Overgeslagen");
            lines.Add(string.Empty);
            lines.AddRange(summary.SkippedTechnicians.Select(skipped => "- " + skipped));
        }

        foreach (var technician in result.Technicians.Where(item => item.Processed))
        {
            lines.Add(string.Empty);
            lines.Add(
                "## " + technician.Technician?.Name +
                " (driverid " + technician.DriverId + ", " + technician.DriverName + ")");
            lines.Add(string.Empty);
            lines.Add(
                "| Datum | Driver | Voertuigen | Eerste werklocatie | Plenion start | Plenion einde | Vertrek laatste | Δ start | Δ einde | Prestaties | Klantstops | Confirmed | Probable | Manual | None | Kwaliteit |");
            lines.Add(
                "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var day in technician.Days)
            {
                var vehicles = string.Join(
                    "; ",
                    day.Vehicles.Select(vehicle =>
                        vehicle.ObjectName + "/" + vehicle.ObjectPlate));
                lines.Add(
                    string.Create(
                        culture,
                        $"| {day.Date:dd/MM/yyyy} | {day.DriverId}/{day.DriverName} | {vehicles} | " +
                        $"{day.FirstWorkLocation?.Timestamp:HH:mm} {day.FirstWorkLocation?.Address} | " +
                        $"{day.FirstPlenionStart:HH:mm} | {day.LastPlenionEnd:HH:mm} | " +
                        $"{day.LastWorkLocationDeparture:HH:mm} | {Format(day.StartDifferenceMinutes)} | " +
                        $"{Format(day.EndDifferenceMinutes)} | {day.PlenionPerformanceCount} | " +
                        $"{day.LinkedCustomerStopCount} | {day.ConfirmedLocationMatchCount} | " +
                        $"{day.ProbableLocationMatchCount} | {day.ManualReviewRequiredCount} | " +
                        $"{day.NoReliableMatchCount} | {day.DataQuality} |"));
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Observaties");
        lines.Add(string.Empty);
        lines.AddRange(result.Observations.Select(observation => "- " + observation));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToJson(BroaderValidationResult result) =>
        JsonSerializer.Serialize(
            new
            {
                result.FromDate,
                result.ThroughDate,
                result.Summary,
                Technicians = result.Technicians.Select(technician => new
                {
                    technician.Query,
                    technician.Processed,
                    technician.SkipReason,
                    TechnicianName = technician.Technician?.Name,
                    technician.DriverId,
                    technician.DriverName,
                    Days = technician.Days,
                }),
                result.Observations,
            },
            JsonOptions);

    private static string Format(int? value) =>
        value is null
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{value:+#;-#;0} min");
}

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed class BroaderValidationPilotService(
    IReadOnlyPilotService pilotService,
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    MatchingOptions matchingOptions,
    ILogger<BroaderValidationPilotService> logger) : IBroaderValidationPilotService
{
    public async Task<BroaderValidationResult> RunAsync(
        BroaderValidationRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var technicians = new List<BroaderValidationTechnicianResult>();
        var observations = new List<string>
        {
            "Bredere validatie is volledig read-only; er is geen Plenion-writeback.",
            "Koppeling gebeurt via Powerfleet driverid/drivername; voertuiggegevens zijn informatief.",
            "Ritten zonder driverid krijgen categorie MissingDriver en tellen niet mee voor urenconclusies.",
        };

        foreach (var technicianRequest in request.Technicians)
        {
            cancellationToken.ThrowIfCancellationRequested();
            technicians.Add(await ProcessTechnicianAsync(
                technicianRequest,
                request,
                observations,
                cancellationToken));
        }

        return new BroaderValidationResult
        {
            FromDate = request.FromDate,
            ThroughDate = request.ThroughDate,
            Technicians = technicians,
            Summary = BuildSummary(technicians),
            Observations = observations
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private async Task<BroaderValidationTechnicianResult> ProcessTechnicianAsync(
        BroaderValidationTechnicianRequest technicianRequest,
        BroaderValidationRequest request,
        List<string> observations,
        CancellationToken cancellationToken)
    {
        try
        {
            var scan = await plenionReader.ReadAsync(
                new ReadOnlyPilotRequest(
                    technicianRequest.TechnicianQuery,
                    request.FromDate,
                    request.ThroughDate,
                    MaximumPerformances: 500),
                cancellationToken);
            var workdays = scan.NormalizedRecords
                .Select(performance => performance.Date)
                .Distinct()
                .Where(date =>
                    date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                .OrderByDescending(date => date)
                .Take(request.MaxWorkingDaysPerTechnician)
                .OrderBy(date => date)
                .ToArray();
            if (workdays.Length == 0)
            {
                return Skipped(
                    technicianRequest.TechnicianQuery,
                    "Geen Plenion-werkdagen in de periode; technieker niet in beide bronnen beschikbaar.");
            }

            var discoveryDriverId = string.IsNullOrWhiteSpace(
                technicianRequest.PowerfleetDriverId)
                ? null
                : technicianRequest.PowerfleetDriverId.Trim();
            var driverId = discoveryDriverId ??
                           await DiscoverDriverIdAcrossDaysAsync(
                               technicianRequest.TechnicianQuery,
                               scan.Technician,
                               workdays,
                               cancellationToken);
            if (string.IsNullOrWhiteSpace(driverId))
            {
                return Skipped(
                    technicianRequest.TechnicianQuery,
                    "Geen betrouwbare Powerfleet-driverid gevonden; technieker niet in beide bronnen beschikbaar.");
            }

            var pilot = await pilotService.RunAsync(
                new ReadOnlyPilotRequest(
                    technicianRequest.TechnicianQuery,
                    workdays[0],
                    workdays[^1],
                    PowerfleetDriverId: driverId,
                    DriverOnlyLinking: true,
                    ResolveAllLocations: true,
                    MaxWorkingDays: request.MaxWorkingDaysPerTechnician,
                    MaximumPerformances: 500,
                    MaximumTrips: 1000,
                    SelectedWorkdays: workdays),
                cancellationToken);
            var driverName = pilot.PowerfleetRecords
                .Select(trip => trip.DriverName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            observations.Add(
                $"{pilot.Technician.Name}: driverid {driverId}; {workdays.Length} werkdagen; " +
                $"{pilot.PlenionRecords.Count} prestaties; {pilot.PowerfleetMatchedCount} gekoppelde ritten.");
            return new BroaderValidationTechnicianResult
            {
                Query = technicianRequest.TechnicianQuery,
                Processed = true,
                Technician = pilot.Technician,
                DriverId = driverId,
                DriverName = driverName,
                Days = BuildDays(pilot, matchingOptions),
                Issues = pilot.Issues,
                PilotResult = pilot,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bredere validatie overgeslagen voor techniekerquery.");
            return Skipped(
                technicianRequest.TechnicianQuery,
                Redact(exception.Message));
        }
    }

    private async Task<string?> DiscoverDriverIdAcrossDaysAsync(
        string technicianQuery,
        Technician technician,
        IReadOnlyList<DateOnly> workdays,
        CancellationToken cancellationToken)
    {
        foreach (var day in workdays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var discovery = await powerfleetReader.ReadAsync(
                    new ReadOnlyPilotRequest(
                        technicianQuery,
                        day,
                        day,
                        DriverOnlyLinking: true,
                        MaximumTrips: 1000),
                    cancellationToken);
                var driverId = DiscoverDriverId(discovery.NormalizedRecords, technician);
                if (!string.IsNullOrWhiteSpace(driverId))
                {
                    return driverId;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Driverid-detectie mislukt voor {Date}: {Reason}",
                    day,
                    Redact(exception.Message));
            }
        }

        return null;
    }

    internal static string? DiscoverDriverId(
        IReadOnlyList<NormalizedPilotTrip> trips,
        Technician technician)
    {
        var tokens = NameTokens(technician.Name);
        if (tokens.Count < 2)
        {
            return null;
        }

        return trips
            .Where(trip => !string.IsNullOrWhiteSpace(trip.DriverId))
            .Where(trip =>
            {
                var driverTokens = NameTokens(trip.DriverName ?? string.Empty);
                return tokens.All(driverTokens.Contains);
            })
            .GroupBy(trip => trip.DriverId!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private static BroaderValidationDayResult[] BuildDays(
        ReadOnlyPilotResult pilot,
        MatchingOptions matchingOptions) =>
        pilot.DayComparisons.Select(day =>
        {
            var dayTrips = pilot.PowerfleetRecords
                .Where(trip =>
                    DateOnly.FromDateTime(trip.StartDateTime.DateTime) == day.Date)
                .ToArray();
            var dayPerformances = pilot.PlenionRecords
                .Where(performance => performance.Date == day.Date)
                .ToArray();
            var dayResolutions = pilot.LocationResolutions
                .Where(resolution => resolution.Date == day.Date)
                .ToArray();
            var linkedStops = pilot.PerformanceStopMatches
                .Count(match =>
                    match.Date == day.Date &&
                    match.Status is PilotMatchStatus.ExactAddressMatch
                        or PilotMatchStatus.ProbableAddressMatch);
            var missingDriver = pilot.Issues.Count(issue =>
                issue.Category.Equals("MissingDriver", StringComparison.Ordinal) &&
                (issue.RecordId is null ||
                 dayTrips.Any(trip => trip.ExternalId == issue.RecordId) ||
                 string.Equals(
                     issue.RecordId,
                     day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                     StringComparison.Ordinal)));
            var vehicles = dayTrips
                .Select(trip => new BroaderValidationVehicleContext(
                    trip.ObjectId,
                    trip.ObjectName,
                    trip.VehiclePlate))
                .DistinctBy(vehicle =>
                    $"{vehicle.ObjectId}|{vehicle.ObjectName}|{vehicle.ObjectPlate}")
                .ToArray();
            var startAbs = Math.Abs(day.StartDifferenceMinutes ?? 0);
            var endAbs = Math.Abs(day.EndDifferenceMinutes ?? 0);
            return new BroaderValidationDayResult(
                day.Date,
                day.Technician,
                dayTrips.Select(trip => trip.DriverId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)),
                dayTrips.Select(trip => trip.DriverName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                vehicles,
                day.FirstWorkLocation,
                day.FirstPlenionStart,
                day.LastPlenionEnd,
                day.LastWorkLocation?.Timestamp,
                day.StartDifferenceMinutes,
                day.EndDifferenceMinutes,
                day.StartDifferenceRelevant,
                day.EndDifferenceRelevant,
                day.PossibleEmployeeBenefitMinutes,
                day.StartDifferenceRelevant &&
                startAbs >= matchingOptions.IndividualExceptionMinutes,
                day.EndDifferenceRelevant &&
                endAbs >= matchingOptions.IndividualExceptionMinutes,
                day.StartDifferenceRelevant &&
                startAbs >= matchingOptions.HighPriorityExceptionMinutes,
                day.EndDifferenceRelevant &&
                endAbs >= matchingOptions.HighPriorityExceptionMinutes,
                dayPerformances.Length,
                linkedStops,
                dayResolutions.Count(item =>
                    item.MatchStatus ==
                    PilotLocationResolutionStatus.ConfirmedLocationMatch),
                dayResolutions.Count(item =>
                    item.MatchStatus ==
                    PilotLocationResolutionStatus.ProbableLocationMatch),
                dayResolutions.Count(item =>
                    item.MatchStatus ==
                    PilotLocationResolutionStatus.ManualReviewRequired),
                dayResolutions.Count(item =>
                    item.MatchStatus ==
                    PilotLocationResolutionStatus.NoReliableMatch),
                dayResolutions.Count(item =>
                    item.MatchStatus ==
                    PilotLocationResolutionStatus.AddressDataIssue),
                missingDriver,
                day.DataQuality,
                day.IsAbsent ? $"Afwezig: {day.AbsenceReason}" : "Werkdag",
                day.Notes);
        }).ToArray();

    private static BroaderValidationSummary BuildSummary(
        List<BroaderValidationTechnicianResult> technicians)
    {
        var processed = technicians.Where(item => item.Processed).ToArray();
        var days = processed.SelectMany(item => item.Days).ToArray();
        var resolutions = processed
            .SelectMany(item => item.PilotResult?.LocationResolutions ?? [])
            .ToArray();
        var totalResolutions = resolutions.Length;
        var confirmed = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.ConfirmedLocationMatch);
        var probable = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.ProbableLocationMatch);
        var manual = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.ManualReviewRequired);
        var none = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.NoReliableMatch);
        var addressIssues = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.AddressDataIssue);
        double Percent(int count) =>
            totalResolutions == 0
                ? 0
                : Math.Round(100d * count / totalResolutions, 1);
        var recurring = resolutions
            .Where(item =>
                item.MatchStatus is PilotLocationResolutionStatus.AddressDataIssue
                    or PilotLocationResolutionStatus.ManualReviewRequired
                    or PilotLocationResolutionStatus.NoReliableMatch)
            .GroupBy(item => item.DiagnosticCategory, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} ({group.Count()})")
            .Take(10)
            .ToArray();
        var hourDeviations = days.Count(day =>
            day.StartDifferenceRelevant || day.EndDifferenceRelevant);
        var individual = days.Count(day =>
            day.StartExceedsIndividualTolerance || day.EndExceedsIndividualTolerance);
        var highPriority = days.Count(day =>
            day.StartExceedsHighPriorityTolerance || day.EndExceedsHighPriorityTolerance);
        return new BroaderValidationSummary
        {
            ProcessedTechnicianCount = processed.Length,
            SkippedTechnicianCount = technicians.Count - processed.Length,
            WorkdayCount = days.Length,
            TotalPerformanceCount = days.Sum(day => day.PlenionPerformanceCount),
            TotalLocationResolutionCount = totalResolutions,
            ConfirmedLocationMatchCount = confirmed,
            ProbableLocationMatchCount = probable,
            ManualReviewRequiredCount = manual,
            NoReliableMatchCount = none,
            AddressDataIssueCount = addressIssues,
            ConfirmedPercent = Percent(confirmed),
            ProbablePercent = Percent(probable),
            ManualReviewPercent = Percent(manual),
            ReliableMatchPercent = Percent(confirmed + probable),
            MissingDriverTripCount = processed.Sum(item =>
                item.Issues.Count(issue =>
                    issue.Category.Equals("MissingDriver", StringComparison.Ordinal))),
            PossibleHourDeviationCount = hourDeviations,
            IndividualToleranceDeviationCount = individual,
            HighPriorityToleranceDeviationCount = highPriority,
            RecurringAddressProblems = recurring,
            SkippedTechnicians = technicians
                .Where(item => !item.Processed)
                .Select(item => $"{item.Query}: {item.SkipReason}")
                .ToArray(),
        };
    }

    private static BroaderValidationTechnicianResult Skipped(
        string query,
        string reason) =>
        new()
        {
            Query = query,
            Processed = false,
            SkipReason = reason,
            Days = [],
            Issues =
            [
                new PilotIssue("Validatie", null, "Overgeslagen", reason)
            ],
        };

    private static void Validate(BroaderValidationRequest request)
    {
        if (request.Technicians.Count is < 1 or > 5)
        {
            throw new ArgumentException(
                "De bredere validatie verwacht één tot en met vijf techniekers.",
                nameof(request));
        }

        if (request.ThroughDate < request.FromDate)
        {
            throw new ArgumentException(
                "Einddatum ligt vóór begindatum.",
                nameof(request));
        }

        if (request.MaxWorkingDaysPerTechnician is < 1 or > 5)
        {
            throw new ArgumentException(
                "Maximaal vijf representatieve werkdagen per technieker.",
                nameof(request));
        }
    }

    private static HashSet<string> NameTokens(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) !=
                UnicodeCategory.NonSpacingMark)
            {
                builder.Append(
                    char.IsLetterOrDigit(character)
                        ? char.ToLowerInvariant(character)
                        : ' ');
            }
        }

        return builder.ToString()
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Redact(string message) =>
        System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)\b(pwd|password|apikey|key)\s*=\s*[^;\s]+",
            "$1=[afgeschermd]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

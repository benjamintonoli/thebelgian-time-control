using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed class ReadOnlyPilotService(
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    IOptions<PowerfleetOptions> powerfleetOptions,
    MatchingOptions matchingOptions,
    ILogger<ReadOnlyPilotService> logger) : IReadOnlyPilotService
{
    private readonly PowerfleetOptions _powerfleetOptions = powerfleetOptions.Value;
    private readonly MatchingOptions _matchingOptions = matchingOptions;

    public async Task<ReadOnlyPilotResult> RunAsync(
        ReadOnlyPilotRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var plenion = await ReadPlenionAsync(request, cancellationToken);
        var powerfleet = await ReadPowerfleetAsync(request, cancellationToken);
        var matchedTrips = powerfleet.NormalizedRecords
            .Where(trip => MatchesPilot(trip, plenion.Technician, request))
            .OrderBy(trip => trip.StartDateTime)
            .ToArray();
        var issues = plenion.Issues.Concat(powerfleet.Issues).ToList();
        AddAssignmentIssues(matchedTrips, plenion.Technician, request, issues);
        var stops = PilotLocationMatcher.ReconstructStops(matchedTrips, issues);
        var performanceMatches = PilotLocationMatcher.Match(
            plenion.NormalizedRecords
                .Where(performance =>
                    FindAbsence(request, performance.Date) is null)
                .ToArray(),
            stops,
            _matchingOptions);

        var comparisons = Dates(request.FromDate, request.ThroughDate)
            .Select(date => CompareDay(
                date,
                plenion.Technician,
                plenion.NormalizedRecords,
                matchedTrips,
                FindAbsence(request, date),
                issues))
            .ToArray();
        return new ReadOnlyPilotResult
        {
            Technician = plenion.Technician,
            FromDate = request.FromDate,
            ThroughDate = request.ThroughDate,
            RawPlenionRecords = plenion.RawRecords.Take(10).ToArray(),
            RawPowerfleetRecords = powerfleet.RawRecords.Take(10).ToArray(),
            PlenionRecords = plenion.NormalizedRecords,
            PowerfleetRecords = matchedTrips,
            PowerfleetStops = stops,
            PerformanceStopMatches = performanceMatches,
            DayComparisons = comparisons,
            Issues = issues,
            SourceObservations = plenion.Observations
                .Concat(powerfleet.Observations)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            PlenionReadCount = plenion.ReadCount,
            PlenionRejectedCount = plenion.RejectedCount,
            PowerfleetReadCount = powerfleet.ReadCount,
            PowerfleetRejectedCount = powerfleet.RejectedCount,
            PowerfleetMatchedCount = matchedTrips.Length,
            PowerfleetEndpoint = powerfleet.Endpoint,
            PowerfleetFilterSummary = powerfleet.FilterSummary,
            IgnoreDifferenceMinutes = _matchingOptions.IgnoreDifferenceMinutes,
            PatternDifferenceMinutes = _matchingOptions.PatternDifferenceMinutes,
            IndividualExceptionMinutes = _matchingOptions.IndividualExceptionMinutes,
            HighPriorityExceptionMinutes = _matchingOptions.HighPriorityExceptionMinutes,
        };
    }

    private async Task<PlenionPilotReadResult> ReadPlenionAsync(
        ReadOnlyPilotRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await plenionReader.ReadAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var safeMessage = Redact(exception.Message);
            logger.LogWarning(
                "De read-only Plenion-pilot kon de bron niet bereiken: {Reason}",
                safeMessage);
            return new PlenionPilotReadResult(
                new Technician
                {
                    ExternalId = request.TechnicianQuery,
                    Code = request.TechnicianQuery,
                    Name = request.TechnicianQuery,
                    Kind = 1,
                },
                [],
                [],
                [new PilotIssue("Plenion", null, "Onvoldoende gegevens", safeMessage)],
                ["Plenion was tijdens deze uitvoering niet bereikbaar; er is niets geschreven."],
                0,
                0);
        }
    }

    private async Task<PowerfleetPilotReadResult> ReadPowerfleetAsync(
        ReadOnlyPilotRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await powerfleetReader.ReadAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var safeMessage = Redact(exception.Message);
            logger.LogWarning(
                "De read-only Powerfleet-pilot kon geen rapport verwerken: {Reason}",
                safeMessage);
            return new PowerfleetPilotReadResult(
                null,
                "Geen server-side filter uitgevoerd.",
                false,
                [],
                [],
                [new PilotIssue("Powerfleet", null, "Onvoldoende gegevens", safeMessage)],
                [],
                0,
                0);
        }
    }

    private PilotDayComparison CompareDay(
        DateOnly date,
        Technician technician,
        IReadOnlyList<NormalizedPilotPerformance> performances,
        IReadOnlyList<NormalizedPilotTrip> trips,
        PilotAbsence? absence,
        List<PilotIssue> issues)
    {
        var dailyPerformances = performances
            .Where(item => item.Date == date)
            .OrderBy(item => item.StartDateTime)
            .ToArray();
        var dailyTrips = trips
            .Where(item => DateOnly.FromDateTime(item.StartDateTime.DateTime) == date)
            .OrderBy(item => item.StartDateTime)
            .ToArray();
        var firstPerformance = dailyPerformances.FirstOrDefault();
        var lastPerformance = dailyPerformances.LastOrDefault();
        var homeDepartureTrip = dailyTrips.FirstOrDefault(IsHomeDeparture);
        var homeArrivalTrip = dailyTrips.LastOrDefault(IsHomeArrival);
        var homeDeparture = homeDepartureTrip is null
            ? null
            : new PilotLocationContext(
                homeDepartureTrip.StartDateTime,
                PreferredAddress(
                    homeDepartureTrip.StartAddress,
                    homeDepartureTrip.StartLocation),
                homeDepartureTrip.StartArea,
                homeDepartureTrip.StartAreaGroup,
                homeDepartureTrip.ExternalId);
        var homeArrival = homeArrivalTrip is null
            ? null
            : new PilotLocationContext(
                homeArrivalTrip.EndDateTime,
                PreferredAddress(
                    homeArrivalTrip.EndAddress,
                    homeArrivalTrip.EndLocation),
                homeArrivalTrip.EndArea,
                homeArrivalTrip.EndAreaGroup,
                homeArrivalTrip.ExternalId);

        if (absence is not null)
        {
            return new PilotDayComparison(
                date,
                technician.Name,
                true,
                $"{absence.Type}: {absence.Reason}",
                null,
                null,
                0,
                0,
                homeDeparture,
                homeArrival,
                null,
                null,
                dailyTrips.Sum(item => item.DrivingMinutes),
                dailyTrips.Sum(item => item.DistanceKilometres),
                null,
                null,
                false,
                false,
                0,
                "Geldige afwezigheid",
                $"{dailyTrips.Length} Powerfleet-ritten uitsluitend informatief; geen werkurenvergelijking.");
        }

        var firstLocation = firstPerformance is null
            ? null
            : FindFirstWorkLocation(dailyTrips, firstPerformance.StartDateTime);
        var lastLocation = lastPerformance is null
            ? null
            : FindLastWorkLocation(dailyTrips, lastPerformance.EndDateTime);
        var deviation = firstPerformance is not null &&
                        lastPerformance is not null &&
                        firstLocation is { Reliable: true } &&
                        lastLocation is { Reliable: true }
            ? PilotDeviationRules.Evaluate(
                firstPerformance.StartDateTime,
                firstLocation.Timestamp,
                lastPerformance.EndDateTime,
                lastLocation.Timestamp,
                _matchingOptions.IgnoreDifferenceMinutes)
            : null;
        int? startDifference = deviation?.StartDifferenceMinutes;
        int? endDifference = deviation?.EndDifferenceMinutes;
        var startRelevant = deviation?.StartRelevant == true;
        var endRelevant = deviation?.EndRelevant == true;
        var possibleBenefit = deviation?.PossibleEmployeeBenefitMinutes ?? 0;
        var quality = new List<string>();
        var notes = new List<string>
        {
            $"{dailyPerformances.Length} Plenion-prestaties; {dailyTrips.Length} gefilterde ritten.",
        };

        if (dailyPerformances.Length == 0 || dailyTrips.Length == 0)
        {
            quality.Add("Onvoldoende gegevens");
        }

        if (dailyTrips.Length == 0)
        {
            quality.Add("Bestuurder ontbreekt");
        }

        if (firstPerformance is not null && firstLocation is not { Reliable: true })
        {
            quality.Add("Werklocatie nog niet betrouwbaar gekoppeld");
            issues.Add(new PilotIssue(
                "Locatiekoppeling",
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "Werklocatie nog niet betrouwbaar gekoppeld",
                "De eerste werklocatie kon niet logisch uit een niet-thuis-stop worden bevestigd."));
        }

        if (lastPerformance is not null && lastLocation is not { Reliable: true })
        {
            quality.Add("Werklocatie nog niet betrouwbaar gekoppeld");
            issues.Add(new PilotIssue(
                "Locatiekoppeling",
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "Werklocatie nog niet betrouwbaar gekoppeld",
                "De laatste werklocatie kon niet logisch uit een niet-thuis-stop worden bevestigd."));
        }

        if (firstLocation is { Reliable: true } &&
            string.IsNullOrWhiteSpace(firstLocation.Area) &&
            string.IsNullOrWhiteSpace(firstLocation.AreaGroup))
        {
            quality.Add("Locatietype niet expliciet bevestigd");
            notes.Add("Bij de eerste kandidaat ontbreken Powerfleet-gebied en -gebiedsgroep.");
        }

        if (lastLocation is { Reliable: true } &&
            string.IsNullOrWhiteSpace(lastLocation.Area) &&
            string.IsNullOrWhiteSpace(lastLocation.AreaGroup))
        {
            quality.Add("Locatietype niet expliciet bevestigd");
            notes.Add("Bij de laatste kandidaat ontbreken Powerfleet-gebied en -gebiedsgroep.");
        }

        if (startDifference is not null)
        {
            notes.Add(startDifference > 0
                ? startRelevant
                    ? "Positieve startafwijking boven tolerantie."
                    : "Positieve startafwijking binnen tolerantie."
                : "Startafwijking is nul of negatief en alleen informatief.");
        }

        if (endDifference is not null)
        {
            notes.Add(endDifference > 0
                ? endRelevant
                    ? "Positieve eindafwijking boven tolerantie."
                    : "Positieve eindafwijking binnen tolerantie."
                : "Eindafwijking is nul of negatief en alleen informatief.");
        }

        return new PilotDayComparison(
            date,
            technician.Name,
            false,
            null,
            firstPerformance?.StartDateTime,
            lastPerformance?.EndDateTime,
            dailyPerformances.Sum(item => item.NetMinutes),
            dailyPerformances.Sum(item => item.DistanceKilometres),
            homeDeparture,
            homeArrival,
            firstLocation,
            lastLocation,
            dailyTrips.Sum(item => item.DrivingMinutes),
            dailyTrips.Sum(item => item.DistanceKilometres),
            startDifference,
            endDifference,
            startRelevant,
            endRelevant,
            possibleBenefit,
            quality.Count == 0
                ? "Goed"
                : string.Join(", ", quality.Distinct(StringComparer.Ordinal)),
            string.Join(" ", notes));
    }

    private static PilotWorkLocationCandidate? FindFirstWorkLocation(
        NormalizedPilotTrip[] trips,
        DateTimeOffset firstPlenionStart)
    {
        var candidates = new List<PilotWorkLocationCandidate>();
        for (var index = 0; index < trips.Length - 1; index++)
        {
            var arrival = trips[index];
            var departure = trips[index + 1];
            if (IsHome(
                    arrival.EndArea,
                    arrival.EndAreaGroup) ||
                !HasLocation(
                    arrival.EndAddress,
                    arrival.EndLocation,
                    arrival.EndArea) ||
                !SameLocationAtStop(arrival, departure) ||
                arrival.EndDateTime > firstPlenionStart.AddHours(2) ||
                departure.StartDateTime < firstPlenionStart)
            {
                continue;
            }

            candidates.Add(new PilotWorkLocationCandidate(
                arrival.EndDateTime,
                PreferredAddress(arrival.EndAddress, arrival.EndLocation),
                arrival.EndArea,
                arrival.EndAreaGroup,
                arrival.ExternalId,
                true,
                LocationAssessment(
                    "Niet-thuis-stop loopt door over de eerste Plenion-start.",
                    arrival.EndArea,
                    arrival.EndAreaGroup)));
        }

        return candidates
            .OrderBy(candidate =>
                Math.Abs((candidate.Timestamp - firstPlenionStart).TotalMinutes))
            .FirstOrDefault();
    }

    private static PilotWorkLocationCandidate? FindLastWorkLocation(
        NormalizedPilotTrip[] trips,
        DateTimeOffset lastPlenionEnd)
    {
        var candidates = new List<PilotWorkLocationCandidate>();
        for (var index = 1; index < trips.Length; index++)
        {
            var arrival = trips[index - 1];
            var departure = trips[index];
            if (IsHome(
                    departure.StartArea,
                    departure.StartAreaGroup) ||
                !HasLocation(
                    departure.StartAddress,
                    departure.StartLocation,
                    departure.StartArea) ||
                !SameLocationAtStop(arrival, departure) ||
                arrival.EndDateTime > lastPlenionEnd ||
                departure.StartDateTime < lastPlenionEnd.AddHours(-2))
            {
                continue;
            }

            candidates.Add(new PilotWorkLocationCandidate(
                departure.StartDateTime,
                PreferredAddress(departure.StartAddress, departure.StartLocation),
                departure.StartArea,
                departure.StartAreaGroup,
                departure.ExternalId,
                true,
                LocationAssessment(
                    "Niet-thuis-stop loopt door tot rond het laatste Plenion-einde.",
                    departure.StartArea,
                    departure.StartAreaGroup)));
        }

        return candidates
            .OrderBy(candidate =>
                Math.Abs((candidate.Timestamp - lastPlenionEnd).TotalMinutes))
            .FirstOrDefault();
    }

    private static bool SameLocationAtStop(
        NormalizedPilotTrip arrival,
        NormalizedPilotTrip departure)
    {
        var endKey = LocationKey(
            arrival.EndArea,
            arrival.EndAreaGroup,
            arrival.EndAddress,
            arrival.EndLocation);
        var startKey = LocationKey(
            departure.StartArea,
            departure.StartAreaGroup,
            departure.StartAddress,
            departure.StartLocation);
        return endKey.Length > 0 &&
               endKey.Equals(startKey, StringComparison.Ordinal);
    }

    private static string LocationKey(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            return string.Concat(value
                .Normalize(NormalizationForm.FormD)
                .Where(character =>
                    CharUnicodeInfo.GetUnicodeCategory(character) !=
                    UnicodeCategory.NonSpacingMark)
                .Where(char.IsLetterOrDigit))
                .ToLowerInvariant();
        }

        return string.Empty;
    }

    private static bool IsHomeDeparture(NormalizedPilotTrip trip) =>
        IsHome(trip.StartArea, trip.StartAreaGroup);

    private static bool IsHomeArrival(NormalizedPilotTrip trip) =>
        IsHome(trip.EndArea, trip.EndAreaGroup);

    private static bool IsHome(string? area, string? areaGroup) =>
        Contains(areaGroup, "huisadres") ||
        Contains(areaGroup, "home") ||
        Contains(area, "huisadres");

    private static bool HasLocation(
        string? address,
        string? location,
        string? area) =>
        !string.IsNullOrWhiteSpace(address) ||
        !string.IsNullOrWhiteSpace(location) ||
        !string.IsNullOrWhiteSpace(area);

    private static string? PreferredAddress(string? address, string? location) =>
        string.IsNullOrWhiteSpace(address) ? location : address;

    private static string LocationAssessment(
        string basis,
        string? area,
        string? areaGroup) =>
        string.IsNullOrWhiteSpace(area) && string.IsNullOrWhiteSpace(areaGroup)
            ? $"{basis} Gebied en gebiedsgroep ontbreken; het werkplaatstype is niet expliciet bevestigd."
            : basis;

    private static bool Contains(string? value, string fragment) =>
        value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;

    private static PilotAbsence? FindAbsence(
        ReadOnlyPilotRequest request,
        DateOnly date) =>
        request.Absences?.FirstOrDefault(absence => absence.Date == date);

    private static void AddAssignmentIssues(
        NormalizedPilotTrip[] matchedTrips,
        Technician technician,
        ReadOnlyPilotRequest request,
        List<PilotIssue> issues)
    {
        if (matchedTrips.Length == 0)
        {
            issues.Add(new PilotIssue(
                "Koppeling",
                null,
                "Bestuurder ontbreekt",
                $"Geen Powerfleet-bestuurder kon betrouwbaar aan {technician.Name} worden gekoppeld."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.VehiclePlate) &&
            matchedTrips.Any(trip =>
                !request.VehiclePlate.Equals(
                    trip.VehiclePlate,
                    StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new PilotIssue(
                "Koppeling",
                null,
                "Voertuigtoewijzing onzeker",
                "Niet alle gekoppelde ritten hebben het verwachte kenteken."));
        }
    }

    private static bool MatchesPilot(
        NormalizedPilotTrip trip,
        Technician technician,
        ReadOnlyPilotRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PowerfleetDriverId) &&
            !request.PowerfleetDriverId.Equals(
                trip.DriverId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.PowerfleetObjectId) &&
            !request.PowerfleetObjectId.Equals(
                trip.ObjectId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.VehiclePlate) &&
            !request.VehiclePlate.Equals(
                trip.VehiclePlate,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.PowerfleetDriverId))
        {
            return true;
        }

        var technicianTokens = NameTokens(technician.Name);
        var driverTokens = NameTokens(trip.DriverName ?? string.Empty);
        return technicianTokens.Count >= 2 &&
               technicianTokens.All(driverTokens.Contains);
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

    private string Redact(string message)
    {
        var result = message;
        foreach (var secret in new[]
                 {
                     _powerfleetOptions.ApiKey,
                     _powerfleetOptions.StateId,
                 })
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                result = result.Replace(
                    secret,
                    "[afgeschermd]",
                    StringComparison.Ordinal);
            }
        }

        return Regex.Replace(
            result,
            @"(?i)\b(pwd|password|apikey|key)\s*=\s*[^;\s]+",
            "$1=[afgeschermd]",
            RegexOptions.CultureInvariant);
    }

    private static void Validate(ReadOnlyPilotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TechnicianQuery))
        {
            throw new ArgumentException("Technieker is verplicht.", nameof(request));
        }

        if (request.ThroughDate < request.FromDate)
        {
            throw new ArgumentException(
                "Einddatum ligt vóór begindatum.",
                nameof(request));
        }

        var dates = Dates(request.FromDate, request.ThroughDate).ToArray();
        var workingDays = dates.Count(date =>
            date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday);
        if (workingDays is < 1 or > 3 || dates.Length > 7)
        {
            throw new ArgumentException(
                "De pilotperiode moet één tot en met drie werkdagen bevatten.",
                nameof(request));
        }
    }

    private static IEnumerable<DateOnly> Dates(
        DateOnly fromDate,
        DateOnly throughDate)
    {
        for (var date = fromDate; date <= throughDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                yield return date;
            }
        }
    }

}

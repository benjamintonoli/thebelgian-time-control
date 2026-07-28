using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static partial class PilotLocationMatcher
{
    public static PilotStop[] ReconstructStops(
        NormalizedPilotTrip[] trips,
        List<PilotIssue> issues)
    {
        var stops = new List<PilotStop>();
        foreach (var dailyTrips in trips
                     .GroupBy(trip =>
                         DateOnly.FromDateTime(trip.StartDateTime.DateTime))
                     .OrderBy(group => group.Key))
        {
            var ordered = dailyTrips
                .OrderBy(trip => trip.StartDateTime)
                .ToArray();
            for (var index = 0; index < ordered.Length - 1; index++)
            {
                var incoming = ordered[index];
                var outgoing = ordered[index + 1];
                if (outgoing.StartDateTime < incoming.EndDateTime)
                {
                    issues.Add(new PilotIssue(
                        "Powerfleet-stop",
                        $"{incoming.ExternalId}/{outgoing.ExternalId}",
                        "Parseprobleem",
                        "Opeenvolgende ritten overlappen; stop is niet aangemaakt."));
                    continue;
                }

                var incomingAddress = Preferred(
                    incoming.EndAddress,
                    incoming.EndLocation);
                var outgoingAddress = Preferred(
                    outgoing.StartAddress,
                    outgoing.StartLocation);
                var continuity = SameLocation(
                    incomingAddress,
                    incoming.EndLatitude,
                    incoming.EndLongitude,
                    outgoingAddress,
                    outgoing.StartLatitude,
                    outgoing.StartLongitude);
                var address = incomingAddress ?? outgoingAddress;
                var parsedAddress = ParseAddress(address);
                var duration = (int)Math.Round(
                    (outgoing.StartDateTime - incoming.EndDateTime).TotalMinutes,
                    MidpointRounding.AwayFromZero);
                stops.Add(new PilotStop(
                    $"{incoming.ExternalId}/{outgoing.ExternalId}",
                    dailyTrips.Key,
                    incoming.ExternalId,
                    outgoing.ExternalId,
                    incoming.EndDateTime,
                    outgoing.StartDateTime,
                    duration,
                    address,
                    parsedAddress.PostalCode,
                    parsedAddress.City,
                    parsedAddress.Street,
                    incoming.EndArea ?? outgoing.StartArea,
                    incoming.EndAreaGroup ?? outgoing.StartAreaGroup,
                    incoming.EndLatitude ?? outgoing.StartLatitude,
                    incoming.EndLongitude ?? outgoing.StartLongitude,
                    incoming.VehiclePlate ?? outgoing.VehiclePlate,
                    incoming.DriverId ?? outgoing.DriverId,
                    incoming.DriverName ?? outgoing.DriverName,
                    continuity,
                    continuity
                        ? "Aankomst- en vertrekpunt zijn adresmatig of coördinaatmatig gelijk."
                        : "Aankomst- en vertrekpunt verschillen; stopadres is onzeker."));
            }
        }

        return stops.ToArray();
    }

    public static PilotPerformanceStopMatch[] Match(
        NormalizedPilotPerformance[] performances,
        PilotStop[] stops,
        MatchingOptions options)
    {
        return performances
            .OrderBy(performance => performance.StartDateTime)
            .Select(performance => MatchOne(
                performance,
                stops.Where(stop =>
                        stop.Date == performance.Date &&
                        stop.LocationContinuity)
                    .ToArray(),
                options))
            .ToArray();
    }

    private static PilotPerformanceStopMatch MatchOne(
        NormalizedPilotPerformance performance,
        PilotStop[] stops,
        MatchingOptions options)
    {
        var expectedAddress = JoinAddress(
            performance.Street,
            performance.PostalCode,
            performance.City,
            performance.Country);
        var candidates = stops
            .Select(stop => Score(performance, expectedAddress, stop, options))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.OverlapMinutes)
            .ThenBy(candidate => candidate.Stop.Arrival)
            .ToArray();
        if (candidates.Length == 0 || candidates[0].Score == 0)
        {
            return new PilotPerformanceStopMatch(
                performance.ExternalId,
                performance.Date,
                performance.StartDateTime,
                performance.EndDateTime,
                expectedAddress,
                performance.PostalCode,
                performance.City,
                null,
                "Geen bruikbare adres- of tijdsovereenkomst.",
                0,
                PilotMatchStatus.NoMatch,
                0,
                "Geen continue Powerfleet-stop past bij deze prestatie.",
                []);
        }

        var best = candidates[0];
        var closeAlternatives = candidates
            .Skip(1)
            .Where(candidate =>
                candidate.Score >= best.Score - 5 &&
                candidate.Score > 0)
            .ToArray();
        var ambiguous = closeAlternatives.Length > 0;
        var finalStatus = ambiguous
            ? PilotMatchStatus.Ambiguous
            : best.Status;
        var alternatives = candidates
            .Skip(ambiguous ? 0 : 1)
            .Where(candidate => candidate.Score > 0)
            .Take(3)
            .Select(candidate => new PilotMatchAlternative(
                candidate.Stop.StopId,
                candidate.Stop.Arrival,
                candidate.Stop.Departure,
                candidate.Stop.Address,
                candidate.Score,
                candidate.Status,
                candidate.Reasons))
            .ToArray();
        return new PilotPerformanceStopMatch(
            performance.ExternalId,
            performance.Date,
            performance.StartDateTime,
            performance.EndDateTime,
            expectedAddress,
            performance.PostalCode,
            performance.City,
            ambiguous ? null : best.Stop,
            best.AddressComparison,
            best.OverlapMinutes,
            finalStatus,
            best.Score,
            ambiguous
                ? $"Meerdere stops liggen binnen 5 scorepunten. Beste redenen: {best.Reasons}"
                : best.Reasons,
            alternatives);
    }

    private static ScoredStop Score(
        NormalizedPilotPerformance performance,
        string? expectedAddress,
        PilotStop stop,
        MatchingOptions options)
    {
        var reasons = new List<string>();
        var addressScore = 0;
        var expectedNormalized = NormalizeAddress(expectedAddress);
        var actualNormalized = NormalizeAddress(stop.Address);
        var exactAddress =
            expectedNormalized.Length > 0 &&
            expectedNormalized.Equals(actualNormalized, StringComparison.Ordinal);
        if (exactAddress)
        {
            addressScore += 60;
            reasons.Add("volledig genormaliseerd adres gelijk (+60)");
        }

        var expectedStreet = NormalizeStreet(performance.Street);
        var actualStreet = NormalizeStreet(stop.Street);
        if (expectedStreet.Length > 0 && actualStreet.Length > 0)
        {
            if (expectedStreet.Equals(actualStreet, StringComparison.Ordinal))
            {
                addressScore += 25;
                reasons.Add("straatnaam gelijk (+25)");
            }
            else if (expectedStreet.Contains(actualStreet, StringComparison.Ordinal) ||
                     actualStreet.Contains(expectedStreet, StringComparison.Ordinal))
            {
                addressScore += 15;
                reasons.Add("straatnaam gedeeltelijk gelijk (+15)");
            }
        }

        if (!string.IsNullOrWhiteSpace(performance.PostalCode) &&
            performance.PostalCode.Equals(
                stop.PostalCode,
                StringComparison.OrdinalIgnoreCase))
        {
            addressScore += 15;
            reasons.Add("postcode gelijk (+15)");
        }

        if (NormalizeAddress(performance.City) is { Length: > 0 } expectedCity &&
            expectedCity.Equals(
                NormalizeAddress(stop.City),
                StringComparison.Ordinal))
        {
            addressScore += 10;
            reasons.Add("gemeente gelijk (+10)");
        }

        var overlap = OverlapMinutes(
            performance.StartDateTime,
            performance.EndDateTime,
            stop.Arrival,
            stop.Departure);
        var timeScore = 0;
        if (overlap > 0)
        {
            timeScore = 25;
            reasons.Add($"tijdsoverlap {overlap} min (+25)");
            if (stop.Arrival <= performance.StartDateTime &&
                stop.Departure >= performance.EndDateTime)
            {
                timeScore += 5;
                reasons.Add("stop omsluit volledige prestatie (+5)");
            }
        }
        else
        {
            var nearestBoundary = new[]
            {
                Math.Abs((stop.Arrival - performance.StartDateTime).TotalMinutes),
                Math.Abs((stop.Departure - performance.EndDateTime).TotalMinutes),
            }.Min();
            if (nearestBoundary <= options.IgnoreDifferenceMinutes)
            {
                timeScore = 10;
                reasons.Add(
                    $"tijdgrens binnen {options.IgnoreDifferenceMinutes} min (+10)");
            }
        }

        var score = Math.Min(100, addressScore + timeScore);
        var status = exactAddress
            ? PilotMatchStatus.ExactAddressMatch
            : addressScore >= 35
                ? PilotMatchStatus.ProbableAddressMatch
                : overlap > 0
                    ? PilotMatchStatus.TimeOnlyMatch
                    : PilotMatchStatus.NoMatch;
        var comparison = exactAddress
            ? "Volledig genormaliseerd adres gelijk."
            : addressScore > 0
                ? $"Gedeeltelijke adresovereenkomst ({addressScore} adrespunten)."
                : string.IsNullOrWhiteSpace(expectedAddress)
                    ? "Plenion bevat geen verwacht adres."
                    : "Geen adresovereenkomst.";
        return new ScoredStop(
            stop,
            score,
            overlap,
            status,
            comparison,
            string.Join("; ", reasons));
    }

    private static int OverlapMinutes(
        DateTimeOffset firstStart,
        DateTimeOffset firstEnd,
        DateTimeOffset secondStart,
        DateTimeOffset secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end <= start
            ? 0
            : (int)Math.Round(
                (end - start).TotalMinutes,
                MidpointRounding.AwayFromZero);
    }

    private static bool SameLocation(
        string? firstAddress,
        decimal? firstLatitude,
        decimal? firstLongitude,
        string? secondAddress,
        decimal? secondLatitude,
        decimal? secondLongitude)
    {
        var firstNormalized = NormalizeAddress(firstAddress);
        var secondNormalized = NormalizeAddress(secondAddress);
        if (firstNormalized.Length > 0 &&
            firstNormalized.Equals(secondNormalized, StringComparison.Ordinal))
        {
            return true;
        }

        return firstLatitude is not null &&
               firstLongitude is not null &&
               secondLatitude is not null &&
               secondLongitude is not null &&
               Math.Abs(firstLatitude.Value - secondLatitude.Value) <= 0.0001m &&
               Math.Abs(firstLongitude.Value - secondLongitude.Value) <= 0.0001m;
    }

    private static ParsedAddress ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new ParsedAddress(null, null, null);
        }

        var postalMatch = BelgianPostalCodeRegex().Match(address);
        var postalCode = postalMatch.Success ? postalMatch.Value : null;
        var street = address.Split(',', StringSplitOptions.TrimEntries)[0];
        string? city = null;
        if (postalMatch.Success)
        {
            var afterPostalCode = address[(postalMatch.Index + postalMatch.Length)..]
                .Trim(' ', ',');
            city = afterPostalCode
                .Split(',', StringSplitOptions.TrimEntries)[0]
                .Trim();
        }

        return new ParsedAddress(street, postalCode, city);
    }

    private static string? JoinAddress(params string?[] parts)
    {
        var values = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        return values.Length == 0 ? null : string.Join(", ", values);
    }

    private static string NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) !=
                UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string NormalizeStreet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var street = value.Split(',', StringSplitOptions.TrimEntries)[0];
        return NormalizeAddress(TrailingHouseNumberRegex().Replace(street, string.Empty));
    }

    private static string? Preferred(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex BelgianPostalCodeRegex();

    [GeneratedRegex(
        @"\s+\d+[A-Za-z]?(?:\s*[-/]\s*\d+[A-Za-z]?)?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TrailingHouseNumberRegex();

    private sealed record ParsedAddress(
        string? Street,
        string? PostalCode,
        string? City);

    private sealed record ScoredStop(
        PilotStop Stop,
        int Score,
        int OverlapMinutes,
        PilotMatchStatus Status,
        string AddressComparison,
        string Reasons);
}

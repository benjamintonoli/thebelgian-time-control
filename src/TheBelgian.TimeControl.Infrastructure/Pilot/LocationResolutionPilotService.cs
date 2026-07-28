using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed partial class LocationResolutionPilotService(
    LocationGeocodingCache cache,
    IDistanceCalculator distanceCalculator,
    LocationMatchingOptions options)
{
    private static readonly DateOnly FirstPilotDate = new(2026, 7, 23);
    private static readonly DateOnly SecondPilotDate = new(2026, 7, 24);

    public async Task<IReadOnlyList<PilotLocationResolution>> ResolveAsync(
        IReadOnlyList<NormalizedPilotPerformance> performances,
        IReadOnlyList<PilotStop> stops,
        CancellationToken cancellationToken)
    {
        var uncertainPerformances = performances
            .Where(IsUncertainPilotLocation)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartDateTime)
            .ToArray();
        var resolvedAddresses =
            new Dictionary<string, CachedGeocodingResult>(StringComparer.Ordinal);
        var results = new List<PilotLocationResolution>();

        foreach (var performance in uncertainPerformances)
        {
            var originalAddress = JoinAddress(
                performance.Street,
                performance.PostalCode,
                performance.City,
                performance.Country);
            var key = performance.DeliveryAddressExternalId ?? originalAddress;
            if (!resolvedAddresses.TryGetValue(key, out var cached))
            {
                cached = await cache.ResolveAsync(
                    performance.DeliveryAddressExternalId,
                    originalAddress,
                    cancellationToken);
                resolvedAddresses[key] = cached;
            }

            var candidates = EvaluateCandidates(
                performance,
                stops.Where(stop =>
                        stop.Date == performance.Date &&
                        stop.LocationContinuity &&
                        stop.Latitude is not null &&
                        stop.Longitude is not null &&
                        !IsHome(stop))
                    .ToArray(),
                cached.Geocoding,
                options,
                distanceCalculator);
            var matchStatus = ResolveStatus(cached.Geocoding, candidates);
            var category = DiagnosticCategory(performance, candidates);
            results.Add(new PilotLocationResolution(
                performance.ExternalId,
                performance.Date,
                performance.ProjectNumber,
                performance.ProjectName,
                performance.WorkOrderNumber,
                performance.StartDateTime,
                performance.EndDateTime,
                performance.DeliveryAddressExternalId,
                originalAddress,
                cached.NormalizedAddress,
                cached.AddressHash,
                cached.Geocoding,
                candidates,
                matchStatus,
                category,
                Assessment(cached.Geocoding, candidates, matchStatus, category)));
        }

        return results;
    }

    internal static IReadOnlyList<PilotLocationCandidateScore> EvaluateCandidates(
        NormalizedPilotPerformance performance,
        IReadOnlyList<PilotStop> stops,
        GeocodingResult geocoding,
        LocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var expectedCoordinate = geocoding.Primary?.Coordinate;
        return stops.Select(stop =>
            {
                double? distance = null;
                PilotDistanceClassification? classification = null;
                var distanceScore = 0;
                if (expectedCoordinate is not null &&
                    stop.Latitude is not null &&
                    stop.Longitude is not null)
                {
                    distance = distanceCalculator.DistanceMetres(
                        expectedCoordinate.Value,
                        new GeoCoordinate(
                            (double)stop.Latitude.Value,
                            (double)stop.Longitude.Value));
                    classification = ClassifyDistance(distance.Value, options);
                    distanceScore = classification switch
                    {
                        PilotDistanceClassification.StrongLocationMatch => 40,
                        PilotDistanceClassification.PossibleLocationMatch => 25,
                        _ => 0,
                    };
                }

                var (addressScore, addressReasons) = AddressScore(
                    performance,
                    stop);
                var overlap = OverlapMinutes(
                    performance.StartDateTime,
                    performance.EndDateTime,
                    stop.Arrival,
                    stop.Departure);
                var timeScore = overlap > 0 ? 30 : 0;
                var timeReasons = new List<string>();
                if (overlap > 0)
                {
                    timeReasons.Add($"tijdsoverlap {overlap} min (+30)");
                    if (stop.Arrival <= performance.StartDateTime &&
                        stop.Departure >= performance.EndDateTime)
                    {
                        timeScore += 5;
                        timeReasons.Add("stop omsluit prestatie (+5)");
                    }
                }
                else
                {
                    var boundaryGap = Math.Min(
                        Math.Abs(
                            (stop.Arrival - performance.EndDateTime)
                            .TotalMinutes),
                        Math.Abs(
                            (stop.Departure - performance.StartDateTime)
                            .TotalMinutes));
                    if (boundaryGap <= 3)
                    {
                        timeScore = 15;
                        timeReasons.Add(
                            $"tijdsgrens binnen 3 min ({boundaryGap:0.##} min, +15)");
                    }
                }

                var totalScore = Math.Min(
                    100,
                    addressScore + distanceScore + timeScore);
                var candidateStatus = CandidateStatus(
                    classification,
                    overlap,
                    addressScore,
                    timeScore,
                    totalScore);
                var reasons = addressReasons
                    .Concat(classification is null
                        ? ["afstand niet beschikbaar"]
                        : [$"{classification}: {distance:0} m (+{distanceScore})"])
                    .Concat(timeReasons);
                return new PilotLocationCandidateScore(
                    stop,
                    distance,
                    classification,
                    overlap,
                    RoundedMinutes(stop.Arrival - performance.StartDateTime),
                    RoundedMinutes(stop.Departure - performance.EndDateTime),
                    addressScore,
                    distanceScore,
                    timeScore,
                    totalScore,
                    candidateStatus,
                    string.Join("; ", reasons));
            })
            .OrderByDescending(item => item.TotalScore)
            .ThenBy(item => item.DistanceMeters ?? double.MaxValue)
            .ThenByDescending(item => item.TimeOverlapMinutes)
            .ToArray();
    }

    internal static PilotDistanceClassification ClassifyDistance(
        double distanceMeters,
        LocationMatchingOptions options) =>
        distanceMeters <= options.StrongMatchMeters
            ? PilotDistanceClassification.StrongLocationMatch
            : distanceMeters <= options.PossibleMatchMeters
                ? PilotDistanceClassification.PossibleLocationMatch
                : PilotDistanceClassification.LocationMismatch;

    internal static PilotLocationResolutionStatus ResolveStatus(
        GeocodingResult geocoding,
        IReadOnlyList<PilotLocationCandidateScore> candidates)
    {
        if (geocoding.Status is GeocodingStatus.NotConfigured or
            GeocodingStatus.NotProcessed or
            GeocodingStatus.ProviderError)
        {
            return PilotLocationResolutionStatus.NoReliableMatch;
        }

        if (geocoding.Status == GeocodingStatus.InvalidAddress)
        {
            return PilotLocationResolutionStatus.AddressDataIssue;
        }

        if (candidates.Count == 0)
        {
            return PilotLocationResolutionStatus.NoReliableMatch;
        }

        var best = candidates[0];
        if (candidates.Count > 1 &&
            best.TotalScore - candidates[1].TotalScore <= 5)
        {
            return PilotLocationResolutionStatus.ManualReviewRequired;
        }

        if (geocoding.Status == GeocodingStatus.Ambiguous)
        {
            return best.MatchStatus ==
                   PilotLocationResolutionStatus.NoReliableMatch
                ? PilotLocationResolutionStatus.AddressDataIssue
                : PilotLocationResolutionStatus.ManualReviewRequired;
        }

        if (geocoding.Status == GeocodingStatus.LowConfidence)
        {
            return best.MatchStatus is
                PilotLocationResolutionStatus.ConfirmedLocationMatch or
                PilotLocationResolutionStatus.ProbableLocationMatch
                ? PilotLocationResolutionStatus.ProbableLocationMatch
                : PilotLocationResolutionStatus.AddressDataIssue;
        }

        return best.MatchStatus;
    }

    private static PilotLocationResolutionStatus CandidateStatus(
        PilotDistanceClassification? distance,
        int overlap,
        int addressScore,
        int timeScore,
        int totalScore)
    {
        if (distance == PilotDistanceClassification.StrongLocationMatch &&
            overlap > 0 &&
            addressScore >= 20 &&
            totalScore >= 75)
        {
            return PilotLocationResolutionStatus.ConfirmedLocationMatch;
        }

        if ((distance is PilotDistanceClassification.StrongLocationMatch or
                PilotDistanceClassification.PossibleLocationMatch) &&
            timeScore > 0 &&
            totalScore >= 55)
        {
            return PilotLocationResolutionStatus.ProbableLocationMatch;
        }

        return PilotLocationResolutionStatus.NoReliableMatch;
    }

    private static (int Score, IReadOnlyList<string> Reasons) AddressScore(
        NormalizedPilotPerformance performance,
        PilotStop stop)
    {
        var score = 0;
        var reasons = new List<string>();
        var expectedStreet = StreetName(performance.Street);
        var actualStreet = StreetName(stop.Street);
        if (expectedStreet.Length > 0 && actualStreet.Length > 0)
        {
            if (expectedStreet.Equals(actualStreet, StringComparison.Ordinal))
            {
                score += 25;
                reasons.Add("straatnaam gelijk (+25)");
            }
            else if (Levenshtein(expectedStreet, actualStreet) <= 2)
            {
                score += 18;
                reasons.Add("straatnaam lijkt een typfout (+18)");
            }
        }

        if (!string.IsNullOrWhiteSpace(performance.PostalCode) &&
            performance.PostalCode.Equals(
                stop.PostalCode,
                StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
            reasons.Add("postcode gelijk (+10)");
        }

        if (Normalize(performance.City).Equals(
                Normalize(stop.City),
                StringComparison.Ordinal) &&
            Normalize(performance.City).Length > 0)
        {
            score += 10;
            reasons.Add("gemeente gelijk (+10)");
        }

        var expectedNumber = HouseNumber(performance.Street);
        var actualNumber = HouseNumber(stop.Street);
        if (expectedNumber.Length > 0 &&
            expectedNumber.Equals(actualNumber, StringComparison.Ordinal))
        {
            score += 5;
            reasons.Add("huisnummer gelijk (+5)");
        }

        return (score, reasons);
    }

    private static bool IsUncertainPilotLocation(
        NormalizedPilotPerformance performance)
    {
        if (performance.Date != FirstPilotDate &&
            performance.Date != SecondPilotDate)
        {
            return false;
        }

        var street = Normalize(performance.Street);
        var customer = Normalize(performance.CustomerOrSiteName);
        return street.Contains("starrenfoflaan", StringComparison.Ordinal) ||
               street.Contains("harelbekestraat", StringComparison.Ordinal) ||
               street.Contains("kapucijnenstraat", StringComparison.Ordinal) ||
               customer.Contains("hoteleurope", StringComparison.Ordinal);
    }

    private static bool IsHome(PilotStop stop) =>
        (stop.Area?.Contains(
             "Huisadres",
             StringComparison.OrdinalIgnoreCase) ?? false) ||
        (stop.AreaGroup?.Contains(
             "Huisadres",
             StringComparison.OrdinalIgnoreCase) ?? false);

    private static string DiagnosticCategory(
        NormalizedPilotPerformance performance,
        IReadOnlyList<PilotLocationCandidateScore> candidates)
    {
        var street = Normalize(performance.Street);
        if (street.Contains("starrenfoflaan", StringComparison.Ordinal))
        {
            return "Waarschijnlijke typfout";
        }

        if (street.Contains("harelbekestraat", StringComparison.Ordinal))
        {
            return "Mogelijk foutief huisnummer";
        }

        if (candidates.Count > 1 &&
            candidates[0].TotalScore - candidates[1].TotalScore <= 5)
        {
            return "Meerdere mogelijke locaties";
        }

        return "Mogelijk verouderd adres of parking/toegang in andere straat";
    }

    private static string Assessment(
        GeocodingResult geocoding,
        IReadOnlyList<PilotLocationCandidateScore> candidates,
        PilotLocationResolutionStatus status,
        string category)
    {
        if (geocoding.Status == GeocodingStatus.NotConfigured)
        {
            return "Geocoding is niet geconfigureerd; er is geen externe aanvraag uitgevoerd.";
        }

        if (geocoding.Status == GeocodingStatus.ProviderError)
        {
            return "De provider gaf een veilige foutstatus; er is geen kandidaat gekozen.";
        }

        if (candidates.Count == 0)
        {
            return "Geen Powerfleet-stop met geldige coördinaten beschikbaar.";
        }

        return $"{category}. Beste kandidaat: {candidates[0].Stop.Address}; " +
               $"score {candidates[0].TotalScore}/100. Eindstatus: {status}.";
    }

    private static int OverlapMinutes(
        DateTimeOffset firstStart,
        DateTimeOffset firstEnd,
        DateTimeOffset secondStart,
        DateTimeOffset secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end <= start ? 0 : RoundedMinutes(end - start);
    }

    private static int RoundedMinutes(TimeSpan value) =>
        (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);

    private static string StreetName(string? value) =>
        Normalize(TrailingHouseNumberRegex().Replace(value ?? string.Empty, ""));

    private static string HouseNumber(string? value)
    {
        var match = TrailingHouseNumberRegex().Match(value ?? string.Empty);
        return match.Success ? Normalize(match.Value) : string.Empty;
    }

    private static string Normalize(string? value)
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

    private static int Levenshtein(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            var current = new int[second.Length + 1];
            current[0] = firstIndex;
            for (var secondIndex = 1;
                 secondIndex <= second.Length;
                 secondIndex++)
            {
                var cost = first[firstIndex - 1] == second[secondIndex - 1]
                    ? 0
                    : 1;
                current[secondIndex] = Math.Min(
                    Math.Min(
                        current[secondIndex - 1] + 1,
                        previous[secondIndex] + 1),
                    previous[secondIndex - 1] + cost);
            }

            previous = current;
        }

        return previous[second.Length];
    }

    private static string JoinAddress(params string?[] parts) =>
        string.Join(
            ", ",
            parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    [GeneratedRegex(
        @"\s+\d+[A-Za-z]?(?:\s*[-/]\s*\d+[A-Za-z]?)?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TrailingHouseNumberRegex();
}

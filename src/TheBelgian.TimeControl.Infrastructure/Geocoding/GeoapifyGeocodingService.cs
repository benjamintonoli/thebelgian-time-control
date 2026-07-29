using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Geocoding;

internal sealed class GeoapifyGeocodingService(
    HttpClient httpClient,
    IOptions<GeocodingOptions> options) : IGeocodingService
{
    private const double HighConfidenceThreshold = 0.95;
    private const double EquivalentConfidenceDifference = 0.05;
    private readonly GeocodingOptions _options = options.Value;

    public bool IsConfigured =>
        _options.Provider.Equals(
            Provider,
            StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string Provider => "Geoapify";

    public async Task<GeocodingResult> GeocodeAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new GeocodingResult(
                GeocodingStatus.NotConfigured,
                Provider,
                null,
                [],
                "Geocoding:ApiKey is niet geconfigureerd.");
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return new GeocodingResult(
                GeocodingStatus.InvalidAddress,
                Provider,
                null,
                [],
                "Het adres is leeg.");
        }

        var path =
            $"v1/geocode/search?text={Uri.EscapeDataString(address.Trim())}" +
            $"&filter={Uri.EscapeDataString($"countrycode:{_options.CountryCode.ToLowerInvariant()}")}" +
            $"&lang={Uri.EscapeDataString(_options.Language)}" +
            "&limit=5&format=json" +
            $"&apiKey={Uri.EscapeDataString(_options.ApiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GeocodingResult(
                    GeocodingStatus.ProviderError,
                    Provider,
                    null,
                    [],
                    $"Geoapify antwoordde met HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            return Parse(document.RootElement);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return new GeocodingResult(
                GeocodingStatus.ProviderError,
                Provider,
                null,
                [],
                $"Geoapify-aanvraag mislukt ({exception.GetType().Name}).");
        }
    }

    internal static GeocodingResult Parse(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return InvalidResult();
        }

        var rankedCandidates = results
            .EnumerateArray()
            .Select(ParseCandidate)
            .Where(item => item is not null)
            .Cast<RankedCandidate>()
            .OrderByDescending(item => item.Confidence)
            .Take(5)
            .ToArray();
        if (rankedCandidates.Length == 0)
        {
            return InvalidResult();
        }

        var primary = rankedCandidates[0];
        var ambiguous =
            rankedCandidates.Length > 1 &&
            primary.Confidence - rankedCandidates[1].Confidence <=
            EquivalentConfidenceDifference &&
            DistanceMeters(
                primary.Candidate.Coordinate,
                rankedCandidates[1].Candidate.Coordinate) > 100;
        var status = ambiguous
            ? GeocodingStatus.Ambiguous
            : primary.Confidence >= HighConfidenceThreshold
                ? GeocodingStatus.Geocoded
                : GeocodingStatus.LowConfidence;
        return new GeocodingResult(
            status,
            "Geoapify",
            primary.Candidate,
            rankedCandidates.Skip(1)
                .Select(item => item.Candidate)
                .ToArray());
    }

    private static RankedCandidate? ParseCandidate(JsonElement result)
    {
        if (!Number(result, "lat", out var latitude) ||
            !Number(result, "lon", out var longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            return null;
        }

        var confidence = result.TryGetProperty("rank", out var rank) &&
                         Number(rank, "confidence", out var parsedConfidence)
            ? Math.Clamp(parsedConfidence, 0, 1)
            : 0;
        double? building = null;
        double? street = null;
        double? city = null;
        string? matchType = null;
        if (result.TryGetProperty("rank", out rank))
        {
            if (Number(rank, "confidence_building_level", out var buildingLevel))
            {
                building = Math.Clamp(buildingLevel, 0, 1);
            }

            if (Number(rank, "confidence_street_level", out var streetLevel))
            {
                street = Math.Clamp(streetLevel, 0, 1);
            }

            if (Number(rank, "confidence_city_level", out var cityLevel))
            {
                city = Math.Clamp(cityLevel, 0, 1);
            }

            matchType = String(rank, "match_type");
        }

        var candidate = new GeocodingCandidate(
            new GeoCoordinate(latitude, longitude),
            String(result, "formatted"),
            confidence.ToString("0.###", CultureInfo.InvariantCulture),
            String(result, "result_type"),
            string.IsNullOrWhiteSpace(matchType) ? [] : [matchType],
            building,
            street,
            city,
            matchType);
        return new RankedCandidate(candidate, confidence);
    }

    private static bool Number(
        JsonElement parent,
        string property,
        out double value)
    {
        value = default;
        return parent.TryGetProperty(property, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value);
    }

    private static string? String(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double DistanceMeters(
        GeoCoordinate first,
        GeoCoordinate second)
    {
        const double earthRadiusMeters = 6_371_000;
        var latitudeDelta = Radians(second.Latitude - first.Latitude);
        var longitudeDelta = Radians(second.Longitude - first.Longitude);
        var fromLatitude = Radians(first.Latitude);
        var toLatitude = Radians(second.Latitude);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(fromLatitude) * Math.Cos(toLatitude) *
                Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusMeters * 2 *
               Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double Radians(double degrees) =>
        degrees * Math.PI / 180;

    private static GeocodingResult InvalidResult() =>
        new(
            GeocodingStatus.InvalidAddress,
            "Geoapify",
            null,
            [],
            "Geoapify retourneerde geen bruikbare locatie.");

    private sealed record RankedCandidate(
        GeocodingCandidate Candidate,
        double Confidence);
}

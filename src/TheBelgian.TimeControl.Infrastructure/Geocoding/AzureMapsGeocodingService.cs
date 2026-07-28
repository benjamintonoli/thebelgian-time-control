using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Geocoding;

internal sealed class AzureMapsGeocodingService(
    HttpClient httpClient,
    IOptions<GeocodingOptions> options) : IGeocodingService
{
    internal const string ApiVersion = "2026-01-01";
    private readonly GeocodingOptions _options = options.Value;

    public bool IsConfigured =>
        _options.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string Provider => "AzureMaps";

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

        var query = $"{address.Trim()}, {_options.CountryCode}";
        var path =
            $"geocode?api-version={ApiVersion}&top=5" +
            $"&query={Uri.EscapeDataString(query)}" +
            $"&view={Uri.EscapeDataString(_options.CountryCode)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(
            "subscription-key",
            _options.ApiKey);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/geo+json"));
        request.Headers.AcceptLanguage.ParseAdd(_options.Language);

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
                    $"Azure Maps antwoordde met HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            return Parse(document.RootElement, Provider);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return new GeocodingResult(
                GeocodingStatus.ProviderError,
                Provider,
                null,
                [],
                $"Azure Maps-aanvraag mislukt ({exception.GetType().Name}).");
        }
    }

    internal static GeocodingResult Parse(
        JsonElement root,
        string provider = "AzureMaps")
    {
        if (!root.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return new GeocodingResult(
                GeocodingStatus.InvalidAddress,
                provider,
                null,
                [],
                "Azure Maps retourneerde geen locaties.");
        }

        var candidates = features
            .EnumerateArray()
            .Select(ParseCandidate)
            .Where(candidate => candidate is not null)
            .Cast<GeocodingCandidate>()
            .ToArray();
        if (candidates.Length == 0)
        {
            return new GeocodingResult(
                GeocodingStatus.InvalidAddress,
                provider,
                null,
                [],
                "Azure Maps retourneerde geen bruikbare locatie.");
        }

        var primary = candidates[0];
        var ambiguous =
            primary.MatchCodes.Contains(
                "Ambiguous",
                StringComparer.OrdinalIgnoreCase) ||
            (candidates.Length > 1 &&
             ConfidenceRank(candidates[1].Confidence) ==
             ConfidenceRank(primary.Confidence));
        var lowConfidence =
            ConfidenceRank(primary.Confidence) < ConfidenceRank("High") ||
            primary.MatchCodes.Contains(
                "UpHierarchy",
                StringComparer.OrdinalIgnoreCase);
        var status = ambiguous
            ? GeocodingStatus.Ambiguous
            : lowConfidence
                ? GeocodingStatus.LowConfidence
                : GeocodingStatus.Geocoded;
        return new GeocodingResult(
            status,
            provider,
            primary,
            candidates.Skip(1).ToArray());
    }

    private static GeocodingCandidate? ParseCandidate(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        var point = PreferredPoint(properties, feature);
        if (point is null)
        {
            return null;
        }

        var address = properties.TryGetProperty("address", out var addressElement)
            ? String(addressElement, "formattedAddress")
            : null;
        var confidence = String(properties, "confidence");
        var entityType = String(properties, "type");
        var matchCodes = properties.TryGetProperty("matchCodes", out var codes) &&
                         codes.ValueKind == JsonValueKind.Array
            ? codes.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : [];
        return new GeocodingCandidate(
            point.Value,
            address,
            confidence,
            entityType,
            matchCodes);
    }

    private static GeoCoordinate? PreferredPoint(
        JsonElement properties,
        JsonElement feature)
    {
        if (properties.TryGetProperty("geocodePoints", out var points) &&
            points.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in points.EnumerateArray())
            {
                if (point.TryGetProperty("usageTypes", out var usageTypes) &&
                    usageTypes.ValueKind == JsonValueKind.Array &&
                    usageTypes.EnumerateArray().Any(item =>
                        item.ValueKind == JsonValueKind.String &&
                        item.GetString()!.Equals(
                            "Route",
                            StringComparison.OrdinalIgnoreCase)) &&
                    TryCoordinate(point, out var routeCoordinate))
                {
                    return routeCoordinate;
                }
            }

            foreach (var point in points.EnumerateArray())
            {
                if (TryCoordinate(point, out var coordinate))
                {
                    return coordinate;
                }
            }
        }

        return TryCoordinate(feature, out var featureCoordinate)
            ? featureCoordinate
            : null;
    }

    private static bool TryCoordinate(
        JsonElement container,
        out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (!container.TryGetProperty("geometry", out var geometry) ||
            !geometry.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = coordinates.EnumerateArray().Take(2).ToArray();
        if (values.Length != 2 ||
            !values[0].TryGetDouble(out var longitude) ||
            !values[1].TryGetDouble(out var latitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            return false;
        }

        coordinate = new GeoCoordinate(latitude, longitude);
        return true;
    }

    private static string? String(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ConfidenceRank(string? confidence) =>
        confidence?.ToUpperInvariant() switch
        {
            "HIGH" => 3,
            "MEDIUM" => 2,
            "LOW" => 1,
            _ => 0,
        };
}

using System.Net;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;

namespace TheBelgian.TimeControl.Tests;

public sealed class AzureMapsGeocodingServiceTests
{
    [Fact]
    public void Parse_ReturnsSuccessfulGeocodingResult()
    {
        using var json = JsonDocument.Parse(Response(Feature(
            "Teststraat 1, 9000 Gent, België",
            3.72,
            51.05,
            "High",
            "Good")));

        var result = AzureMapsGeocodingService.Parse(json.RootElement);

        Assert.Equal(GeocodingStatus.Geocoded, result.Status);
        Assert.Equal(51.05, result.Primary?.Coordinate.Latitude);
        Assert.Equal(3.72, result.Primary?.Coordinate.Longitude);
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public void Parse_ReturnsInvalidAddressWhenNoResultsExist()
    {
        using var json = JsonDocument.Parse(Response());

        var result = AzureMapsGeocodingService.Parse(json.RootElement);

        Assert.Equal(GeocodingStatus.InvalidAddress, result.Status);
        Assert.Null(result.Primary);
    }

    [Fact]
    public void Parse_ReturnsAmbiguousForEquivalentResults()
    {
        using var json = JsonDocument.Parse(Response(
            Feature("Teststraat 1", 3.72, 51.05, "High", "Good"),
            Feature("Teststraat 1A", 3.721, 51.051, "High", "Good")));

        var result = AzureMapsGeocodingService.Parse(json.RootElement);

        Assert.Equal(GeocodingStatus.Ambiguous, result.Status);
        Assert.Single(result.Alternatives);
    }

    [Fact]
    public void Parse_ReturnsLowConfidenceForHierarchyFallback()
    {
        using var json = JsonDocument.Parse(Response(Feature(
            "9000 Gent, België",
            3.72,
            51.05,
            "Medium",
            "UpHierarchy")));

        var result = AzureMapsGeocodingService.Parse(json.RootElement);

        Assert.Equal(GeocodingStatus.LowConfidence, result.Status);
    }

    [Fact]
    public async Task Geocode_ReturnsProviderErrorWithoutLeakingKey()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://atlas.microsoft.com/"),
        };
        var service = new AzureMapsGeocodingService(
            client,
            Options.Create(new GeocodingOptions
            {
                ApiKey = "unit-test-key",
            }));

        var result = await service.GeocodeAsync(
            "Teststraat 1, 9000 Gent",
            CancellationToken.None);

        Assert.Equal(GeocodingStatus.ProviderError, result.Status);
        Assert.DoesNotContain("unit-test-key", result.ErrorMessage);
        Assert.DoesNotContain(
            "unit-test-key",
            handler.RequestUri?.ToString() ?? string.Empty);
        Assert.Equal(
            "unit-test-key",
            handler.SubscriptionKey);
    }

    [Fact]
    public async Task Geocode_DoesNotCallProviderWhenNotConfigured()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK));
        var service = new AzureMapsGeocodingService(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://atlas.microsoft.com/"),
            },
            Options.Create(new GeocodingOptions()));

        var result = await service.GeocodeAsync(
            "Teststraat 1",
            CancellationToken.None);

        Assert.Equal(GeocodingStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    private static string Response(params string[] features) =>
        $$"""
        {
          "type": "FeatureCollection",
          "features": [{{string.Join(",", features)}}]
        }
        """;

    private static string Feature(
        string address,
        double longitude,
        double latitude,
        string confidence,
        string matchCode) =>
        $$"""
        {
          "type": "Feature",
          "properties": {
            "address": { "formattedAddress": "{{address}}" },
            "type": "Address",
            "confidence": "{{confidence}}",
            "matchCodes": ["{{matchCode}}"],
            "geocodePoints": [{
              "geometry": {
                "type": "Point",
                "coordinates": [{{longitude.ToString(CultureInfo.InvariantCulture)}}, {{latitude.ToString(CultureInfo.InvariantCulture)}}]
              },
              "usageTypes": ["Route"]
            }]
          },
          "geometry": {
            "type": "Point",
            "coordinates": [{{longitude.ToString(CultureInfo.InvariantCulture)}}, {{latitude.ToString(CultureInfo.InvariantCulture)}}]
          }
        }
        """;

    private sealed class RecordingHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? SubscriptionKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            SubscriptionKey = request.Headers
                .GetValues("subscription-key")
                .Single();
            return Task.FromResult(response);
        }
    }
}

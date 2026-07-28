using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;

namespace TheBelgian.TimeControl.Tests;

public sealed class GeoapifyGeocodingServiceTests
{
    [Fact]
    public async Task Geocode_UsesBoundedBelgianForwardSearch()
    {
        var handler = new RecordingHandler(JsonResponse(
            """
            {
              "results": [{
                "formatted": "Teststraat 1, 9000 Gent, België",
                "lat": 51.05,
                "lon": 3.72,
                "result_type": "building",
                "rank": {
                  "confidence": 0.99,
                  "match_type": "full_match"
                }
              }]
            }
            """));
        var service = Service(handler);
        const string address = "Teststraat 1, 9000 Gent, BE";

        var result = await service.GeocodeAsync(
            address,
            CancellationToken.None);

        Assert.Equal(GeocodingStatus.Geocoded, result.Status);
        Assert.Equal("Geoapify", result.Provider);
        Assert.Equal(51.05, result.Primary?.Coordinate.Latitude);
        Assert.Equal(3.72, result.Primary?.Coordinate.Longitude);
        Assert.Equal("0.99", result.Primary?.Confidence);
        Assert.Contains(
            $"text={Uri.EscapeDataString(address)}",
            handler.RequestUri?.Query);
        Assert.Contains("filter=countrycode%3Abe", handler.RequestUri?.Query);
        Assert.Contains("lang=nl", handler.RequestUri?.Query);
        Assert.Contains("limit=5", handler.RequestUri?.Query);
        Assert.Contains("apiKey=unit-test-key", handler.RequestUri?.Query);
    }

    [Fact]
    public void Parse_ReturnsInvalidAddressForNoResults()
    {
        using var document = JsonDocument.Parse("""{"results":[]}""");

        var result = GeoapifyGeocodingService.Parse(document.RootElement);

        Assert.Equal(GeocodingStatus.InvalidAddress, result.Status);
    }

    [Fact]
    public void Parse_PreservesAlternativesAndMarksEquivalentRanksAmbiguous()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "results": [
                {
                  "formatted": "Teststraat 1",
                  "lat": 51.05,
                  "lon": 3.72,
                  "result_type": "building",
                  "rank": {"confidence": 0.98, "match_type": "full_match"}
                },
                {
                  "formatted": "Teststraat 1A",
                  "lat": 51.051,
                  "lon": 3.721,
                  "result_type": "building",
                  "rank": {"confidence": 0.95, "match_type": "full_match"}
                }
              ]
            }
            """);

        var result = GeoapifyGeocodingService.Parse(document.RootElement);

        Assert.Equal(GeocodingStatus.Ambiguous, result.Status);
        Assert.Single(result.Alternatives);
    }

    [Fact]
    public void Parse_TreatsTwoNamesAtSameLocationAsOneLocation()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "results": [
                {
                  "formatted": "Gebouw A, Teststraat 1",
                  "lat": 51.05,
                  "lon": 3.72,
                  "result_type": "building",
                  "rank": {"confidence": 1, "match_type": "full_match"}
                },
                {
                  "formatted": "Organisatie B, Teststraat 1",
                  "lat": 51.05005,
                  "lon": 3.72005,
                  "result_type": "amenity",
                  "rank": {"confidence": 1, "match_type": "full_match"}
                }
              ]
            }
            """);

        var result = GeoapifyGeocodingService.Parse(document.RootElement);

        Assert.Equal(GeocodingStatus.Geocoded, result.Status);
        Assert.Single(result.Alternatives);
    }

    [Fact]
    public void Parse_MarksConfidenceBelowThresholdLow()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "results": [{
                "formatted": "Teststraat, Gent",
                "lat": 51.05,
                "lon": 3.72,
                "result_type": "street",
                "rank": {
                  "confidence": 0.72,
                  "match_type": "match_by_street"
                }
              }]
            }
            """);

        var result = GeoapifyGeocodingService.Parse(document.RootElement);

        Assert.Equal(GeocodingStatus.LowConfidence, result.Status);
        Assert.Equal("0.72", result.Primary?.Confidence);
    }

    [Fact]
    public async Task Geocode_ReturnsSafeProviderError()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var service = Service(handler);

        var result = await service.GeocodeAsync(
            "Teststraat 1",
            CancellationToken.None);

        Assert.Equal(GeocodingStatus.ProviderError, result.Status);
        Assert.DoesNotContain("unit-test-key", result.ErrorMessage);
    }

    [Fact]
    public async Task Geocode_DoesNotCallProviderWithoutKey()
    {
        var handler = new RecordingHandler(JsonResponse("""{"results":[]}"""));
        var service = new GeoapifyGeocodingService(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.geoapify.com/"),
            },
            Options.Create(new GeocodingOptions
            {
                Provider = "Geoapify",
                CountryCode = "be",
                Language = "nl",
            }));

        var result = await service.GeocodeAsync(
            "Teststraat 1",
            CancellationToken.None);

        Assert.Equal(GeocodingStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    private static GeoapifyGeocodingService Service(
        RecordingHandler handler) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.geoapify.com/"),
            },
            Options.Create(new GeocodingOptions
            {
                Provider = "Geoapify",
                ApiKey = "unit-test-key",
                CountryCode = "be",
                Language = "nl",
            }));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class RecordingHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            return Task.FromResult(response);
        }
    }
}

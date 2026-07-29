using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Geocoding;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class AdaptiveLocationMatchingTests
{
    [Theory]
    [InlineData("building", 0.99, 0.95, GeocodeQualityClass.PreciseBuilding)]
    [InlineData("amenity", 0.96, 0.9, GeocodeQualityClass.PreciseAmenity)]
    [InlineData("street", 0.8, 0.0, GeocodeQualityClass.StreetOnly)]
    [InlineData("city", 0.9, 0.0, GeocodeQualityClass.Unusable)]
    public void Classify_UsesResultTypeAndConfidence(
        string resultType,
        double confidence,
        double building,
        GeocodeQualityClass expected)
    {
        var geocoding = new GeocodingResult(
            GeocodingStatus.Geocoded,
            "Geoapify",
            new GeocodingCandidate(
                new GeoCoordinate(51, 3),
                "x",
                confidence.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                resultType,
                ["full_match"],
                building,
                0.8,
                0.8,
                "full_match"),
            []);

        Assert.Equal(expected, GeocodeQualityClassifier.Classify(geocoding));
    }

    [Fact]
    public void Parse_CapturesGeoapifyRankLevels()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "results": [{
                "formatted": "Teststraat 1",
                "lat": 51.05,
                "lon": 3.72,
                "result_type": "building",
                "rank": {
                  "confidence": 0.97,
                  "confidence_building_level": 0.94,
                  "confidence_street_level": 1,
                  "confidence_city_level": 1,
                  "match_type": "full_match"
                }
              }]
            }
            """);

        var result = GeoapifyGeocodingService.Parse(document.RootElement);

        Assert.Equal(0.94, result.Primary?.ConfidenceBuildingLevel);
        Assert.Equal(1, result.Primary?.ConfidenceStreetLevel);
        Assert.Equal("full_match", result.Primary?.MatchType);
        Assert.Equal(
            GeocodeQualityClass.PreciseBuilding,
            GeocodeQualityClassifier.Classify(result));
    }

    [Fact]
    public void Merge_CombinesNearbyFragments_AndFlagsPassThrough()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var stops = new[]
        {
            Stop("a", 51.0m, 3.0m, 8, 0, 8, 1),
            Stop("b", 51.0001m, 3.0001m, 8, 1, 8, 40),
            Stop("c", 51.2m, 3.2m, 10, 0, 10, 1),
        };

        var merged = MergedStopBuilder.Merge(
            stops,
            options,
            new HaversineDistanceCalculator());

        Assert.Equal(2, merged.Count);
        Assert.False(merged[0].IsPassThrough);
        Assert.True(merged[1].IsPassThrough);
        Assert.Equal(2, merged[0].SourceStopIds.Count);
    }

    [Fact]
    public void Match_RequiresTimeSupport_AndScoreMargin()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var performance = Performance(1);
        var resolution = Resolution(performance, 51.05, 3.72, "building", 0.99, 0.95);
        var near = Merged(
            "near",
            51.0501m,
            3.7201m,
            performance.StartDateTime,
            performance.EndDateTime);
        var far = Merged(
            "far",
            51.0502m,
            3.7202m,
            performance.StartDateTime.AddHours(-3),
            performance.StartDateTime.AddHours(-2));

        var result = AdaptiveLocationMatcher.Match(
            performance,
            "Tech",
            resolution,
            [near, far],
            [performance],
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            options,
            new HaversineDistanceCalculator(),
            enableLearning: false);

        Assert.Equal(AdaptiveMatchDecision.Confirmed, result.Decision);
        Assert.Equal("near", result.Selected?.Stop.MergedStopId);
    }

    [Fact]
    public void Hybrid_RecoversStrongUnresolved_WithPositiveOverlap()
    {
        var options = new AdaptiveLocationMatchingOptions();
        // Short performance: 4 min overlap is positive but below adaptive strongTime (5 min).
        var performance = new NormalizedPilotPerformance(
            279763,
            "1",
            new DateOnly(2026, 7, 23),
            DateTimeOffset.Parse("2026-07-23T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T12:10:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            0,
            10,
            10,
            0,
            "p",
            "5",
            "b",
            "Onderhoud",
            null,
            "P1",
            "Project",
            "L1",
            "Klant",
            "Teststraat 1",
            "9000",
            "Gent",
            "BE",
            1,
            1,
            1,
            "ok",
            "ok");
        var resolution = Resolution(performance, 51.05, 3.72, "street", 0.8, 0.0);
        var near = Merged(
            "recover",
            51.0504m,
            3.7204m,
            DateTimeOffset.Parse("2026-07-23T12:06:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T13:04:00+02:00", System.Globalization.CultureInfo.InvariantCulture));

        var adaptive = AdaptiveLocationMatcher.Match(
            performance,
            "Tech",
            resolution,
            [near],
            [performance],
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            options,
            new HaversineDistanceCalculator(),
            enableLearning: false);
        var hybrid = PrecisionPreservingHybridMatcher.Match(
            performance,
            "Tech",
            resolution,
            [near],
            [performance],
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            options,
            new HaversineDistanceCalculator());

        Assert.Equal(AdaptiveMatchDecision.Unresolved, adaptive.Decision);
        Assert.Equal(AdaptiveMatchDecision.Probable, hybrid.Decision);
        Assert.True(hybrid.UsedRecovery);
        Assert.Equal("recover", hybrid.Selected?.Stop.MergedStopId);
        Assert.Contains("Recovery:", hybrid.RecoveryReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hybrid_RejectsStopStartingAfterPerformanceEnd()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var performance = Performance(280198);
        var resolution = Resolution(performance, 51.05, 3.72, "building", 0.99, 0.95);
        var afterEnd = Merged(
            "late",
            51.0505m,
            3.7205m,
            performance.EndDateTime.AddMinutes(3),
            performance.EndDateTime.AddMinutes(23));

        var hybrid = PrecisionPreservingHybridMatcher.Match(
            performance,
            "Tech",
            resolution,
            [afterEnd],
            [performance],
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            options,
            new HaversineDistanceCalculator());

        Assert.False(hybrid.UsedRecovery);
        Assert.True(
            hybrid.Decision is AdaptiveMatchDecision.Unresolved or AdaptiveMatchDecision.Ambiguous);
    }

    private static PilotStop Stop(
        string id,
        decimal lat,
        decimal lon,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute) =>
        new(
            id,
            new DateOnly(2026, 7, 23),
            id + "-in",
            id + "-out",
            DateTimeOffset.Parse(
                $"2026-07-23T{startHour:00}:{startMinute:00}:00+02:00",
                System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(
                $"2026-07-23T{endHour:00}:{endMinute:00}:00+02:00",
                System.Globalization.CultureInfo.InvariantCulture),
            Math.Max(1, (endHour * 60 + endMinute) - (startHour * 60 + startMinute)),
            "Stop",
            null,
            null,
            null,
            null,
            null,
            lat,
            lon,
            null,
            "1",
            "Tech",
            true,
            "ok");

    private static MergedPilotStop Merged(
        string id,
        decimal lat,
        decimal lon,
        DateTimeOffset arrival,
        DateTimeOffset departure) =>
        new(
            id,
            new DateOnly(2026, 7, 23),
            arrival,
            departure,
            Math.Max(1, (int)(departure - arrival).TotalMinutes),
            "Stop " + id,
            lat,
            lon,
            "1",
            "Tech",
            [id],
            false);

    private static NormalizedPilotPerformance Performance(long id) =>
        new(
            id,
            "1",
            new DateOnly(2026, 7, 23),
            DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T09:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            0,
            60,
            60,
            0,
            "p",
            "5",
            "b",
            "Onderhoud",
            null,
            "P1",
            "Project",
            "L1",
            "Klant",
            "Teststraat 1",
            "9000",
            "Gent",
            "BE",
            1,
            1,
            1,
            "ok",
            "ok");

    private static PilotLocationResolution Resolution(
        NormalizedPilotPerformance performance,
        double lat,
        double lon,
        string resultType,
        double confidence,
        double building) =>
        new(
            performance.ExternalId,
            performance.Date,
            performance.ProjectNumber,
            performance.ProjectName,
            performance.WorkOrderNumber,
            performance.StartDateTime,
            performance.EndDateTime,
            performance.DeliveryAddressExternalId,
            "Teststraat 1, 9000 Gent",
            "teststraat19000gent",
            "hash",
            new GeocodingResult(
                GeocodingStatus.Geocoded,
                "Geoapify",
                new GeocodingCandidate(
                    new GeoCoordinate(lat, lon),
                    "Teststraat 1",
                    confidence.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    resultType,
                    ["full_match"],
                    building,
                    1,
                    1,
                    "full_match"),
                []),
            [],
            PilotLocationResolutionStatus.NoReliableMatch,
            "t",
            "t");
}

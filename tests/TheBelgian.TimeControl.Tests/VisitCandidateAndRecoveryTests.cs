using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class VisitCandidateAndRecoveryTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void VisitCandidateBuilder_MergesNearbyFragments_WithinGapAndDistance()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var first = Stop("a", 51.05m, 3.72m, 8, 0, 11, 30);
        var second = Stop("b", 51.0502m, 3.7202m, 11, 35, 16, 0);
        var elsewhere = Stop("c", 51.20m, 3.90m, 16, 30, 17, 0);

        var visits = VisitCandidateBuilder.Build(
            [first, second, elsewhere],
            options,
            new HaversineDistanceCalculator());

        Assert.Equal(2, visits.Count);
        Assert.Equal(2, visits[0].ConstituentStopIds.Count);
        Assert.Contains("a", visits[0].ConstituentStopIds);
        Assert.Contains("b", visits[0].ConstituentStopIds);
        Assert.Equal("c", visits[1].ConstituentStopIds[0]);
    }

    [Fact]
    public void VisitCandidateBuilder_DoesNotMergeAcrossDifferentLocationGap()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var first = Stop("a", 51.05m, 3.72m, 8, 0, 9, 0);
        var intervening = Stop("x", 51.10m, 3.80m, 9, 5, 9, 20);
        var later = Stop("b", 51.05m, 3.72m, 9, 25, 10, 0);

        var visits = VisitCandidateBuilder.Build(
            [first, intervening, later],
            options,
            new HaversineDistanceCalculator());

        Assert.Equal(3, visits.Count);
    }

    [Fact]
    public void Recovery_RejectsWeakOverlap_WithoutShortChain()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var performance = Performance(
            1,
            "2026-07-09T14:40:00+02:00",
            "2026-07-09T14:45:00+02:00",
            lac: "16414");
        var resolution = Resolution(performance, 51.05, 3.72, "street", 0.8, 0.0);
        var weak = Merged(
            "weak",
            51.0505m,
            3.7205m,
            DateTimeOffset.Parse("2026-07-09T14:43:00+02:00", Invariant),
            DateTimeOffset.Parse("2026-07-09T15:44:00+02:00", Invariant));

        var hybrid = PrecisionPreservingHybridMatcher.Match(
            performance,
            "Tech",
            resolution,
            [weak],
            [performance],
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            options,
            new HaversineDistanceCalculator());

        Assert.False(hybrid.UsedRecovery);
        Assert.True(
            hybrid.Decision is AdaptiveMatchDecision.Unresolved or AdaptiveMatchDecision.Ambiguous);
    }

    [Fact]
    public void Recovery_AcceptsShortSameLacChain_WithSharedVisit()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var first = Performance(
            10,
            "2026-07-17T09:40:00+02:00",
            "2026-07-17T10:00:00+02:00",
            lac: "12989");
        var second = Performance(
            11,
            "2026-07-17T10:00:00+02:00",
            "2026-07-17T11:00:00+02:00",
            lac: "12989");
        var resolution = Resolution(second, 51.05, 3.72, "street", 0.8, 0.0);
        var shared = Merged(
            "shared",
            51.0504m,
            3.7204m,
            DateTimeOffset.Parse("2026-07-17T09:53:00+02:00", Invariant),
            DateTimeOffset.Parse("2026-07-17T10:24:00+02:00", Invariant));

        var hybrid = PrecisionPreservingHybridMatcher.Match(
            second,
            "Tech",
            resolution,
            [shared],
            [first, second],
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            options,
            new HaversineDistanceCalculator());

        Assert.True(hybrid.UsedRecovery || hybrid.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable);
        Assert.Equal("shared", hybrid.Selected?.Stop.MergedStopId);
    }

    [Fact]
    public void Recovery_RejectsStopStartingAfterPerformanceEnd()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var performance = Performance(
            280198,
            "2026-07-23T13:10:00+02:00",
            "2026-07-23T14:15:00+02:00",
            lac: "16078");
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

    [Fact]
    public void VisitLabelMatching_RequiresAllExpectedFragments()
    {
        Assert.True(
            VisitLabelMatching.MatchesVisit(
                null,
                ["a", "b"],
                "visit:a+b",
                ["a", "b"]));
        Assert.False(
            VisitLabelMatching.MatchesVisit(
                null,
                ["a", "b"],
                "a",
                ["a"]));
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
                Invariant),
            DateTimeOffset.Parse(
                $"2026-07-23T{endHour:00}:{endMinute:00}:00+02:00",
                Invariant),
            Math.Max(1, (endHour * 60 + endMinute) - (startHour * 60 + startMinute)),
            "Stop " + id,
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
            new DateOnly(2026, 7, 17),
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

    private static NormalizedPilotPerformance Performance(
        long id,
        string start,
        string end,
        string lac) =>
        new(
            id,
            "1",
            DateOnly.FromDateTime(DateTimeOffset.Parse(start, Invariant).DateTime),
            DateTimeOffset.Parse(start, Invariant),
            DateTimeOffset.Parse(end, Invariant),
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
            lac,
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
                    confidence.ToString("0.##", Invariant),
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

using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class LocationResolutionPilotServiceTests
{
    private static readonly LocationMatchingOptions Options = new()
    {
        StrongMatchMeters = 100,
        PossibleMatchMeters = 250,
    };

    [Theory]
    [InlineData(0, PilotDistanceClassification.StrongLocationMatch)]
    [InlineData(100, PilotDistanceClassification.StrongLocationMatch)]
    [InlineData(100.01, PilotDistanceClassification.PossibleLocationMatch)]
    [InlineData(250, PilotDistanceClassification.PossibleLocationMatch)]
    [InlineData(250.01, PilotDistanceClassification.LocationMismatch)]
    public void ClassifyDistance_UsesInclusiveBoundaries(
        double distance,
        PilotDistanceClassification expected)
    {
        Assert.Equal(
            expected,
            LocationResolutionPilotService.ClassifyDistance(distance, Options));
    }

    [Fact]
    public void ResolveStatus_RequiresManualReviewForSimilarCandidates()
    {
        var performance = Performance();
        var geocoding = Geocoding();
        var candidates = LocationResolutionPilotService.EvaluateCandidates(
            performance,
            [
                Stop("first", 51.05m, 3.72m),
                Stop("second", 51.05m, 3.72m),
            ],
            geocoding,
            Options,
            new HaversineDistanceCalculator());

        var status = LocationResolutionPilotService.ResolveStatus(
            geocoding,
            candidates);

        Assert.Equal(PilotLocationResolutionStatus.ManualReviewRequired, status);
    }

    [Fact]
    public void EvaluateCandidates_CombinesDistanceAndTimeOverlap()
    {
        var performance = Performance();
        var geocoding = Geocoding();
        var closeWithoutOverlap = Stop(
            "close",
            51.05m,
            3.72m,
            performance.StartDateTime.AddHours(-2),
            performance.StartDateTime.AddHours(-1)) with
        {
            Address = "Andereweg 9, 9000 Gent",
            Street = "Andereweg 9",
        };
        var possibleWithOverlap = Stop(
            "combined",
            51.051m,
            3.72m,
            performance.StartDateTime,
            performance.EndDateTime);

        var candidates = LocationResolutionPilotService.EvaluateCandidates(
            performance,
            [closeWithoutOverlap, possibleWithOverlap],
            geocoding,
            Options,
            new HaversineDistanceCalculator());

        Assert.Equal("combined", candidates[0].Stop.StopId);
        Assert.True(candidates[0].TimeOverlapMinutes > 0);
        Assert.Equal(
            PilotDistanceClassification.PossibleLocationMatch,
            candidates[0].DistanceClassification);
        Assert.True(candidates[0].TotalScore > candidates[1].TotalScore);
    }

    [Fact]
    public void EvaluateCandidates_UsesThreeMinuteBoundaryAsSupport()
    {
        var performance = Performance();
        var stop = Stop(
            "boundary",
            51.05m,
            3.72m,
            performance.EndDateTime.AddMinutes(2),
            performance.EndDateTime.AddHours(1));

        var candidate = LocationResolutionPilotService.EvaluateCandidates(
            performance,
            [stop],
            Geocoding(),
            Options,
            new HaversineDistanceCalculator()).Single();

        Assert.Equal(0, candidate.TimeOverlapMinutes);
        Assert.Equal(15, candidate.TimeScore);
        Assert.Equal(
            PilotLocationResolutionStatus.ProbableLocationMatch,
            candidate.MatchStatus);
    }

    [Fact]
    public void HaversineDistance_ResolvesLocalMeterScale()
    {
        var calculator = new HaversineDistanceCalculator();

        var distance = calculator.DistanceMetres(
            new GeoCoordinate(51.05, 3.72),
            new GeoCoordinate(51.0509, 3.72));

        Assert.InRange(distance, 99, 101);
    }

    private static GeocodingResult Geocoding() =>
        new(
            GeocodingStatus.Geocoded,
            "Test",
            new GeocodingCandidate(
                new GeoCoordinate(51.05, 3.72),
                "Teststraat 1, 9000 Gent",
                "High",
                "Address",
                ["Good"]),
            []);

    private static NormalizedPilotPerformance Performance()
    {
        var start = new DateTimeOffset(
            2026,
            7,
            23,
            8,
            0,
            0,
            TimeSpan.FromHours(2));
        return new NormalizedPilotPerformance(
            1,
            "resource",
            new DateOnly(2026, 7, 23),
            start,
            start.AddHours(1),
            0,
            60,
            60,
            0,
            "project",
            "task",
            "work-order",
            "Test",
            null,
            "P-1",
            "Testproject",
            "address",
            "Testklant",
            "Teststraat 1",
            "9000",
            "Gent",
            "BE",
            1,
            1,
            1,
            "Uniek",
            "Test");
    }

    private static PilotStop Stop(
        string id,
        decimal latitude,
        decimal longitude,
        DateTimeOffset? arrival = null,
        DateTimeOffset? departure = null)
    {
        var start = new DateTimeOffset(
            2026,
            7,
            23,
            8,
            0,
            0,
            TimeSpan.FromHours(2));
        return new PilotStop(
            id,
            new DateOnly(2026, 7, 23),
            "incoming",
            "outgoing",
            arrival ?? start,
            departure ?? start.AddHours(1),
            60,
            "Teststraat 1, 9000 Gent",
            "9000",
            "Gent",
            "Teststraat 1",
            null,
            null,
            latitude,
            longitude,
            "TEST",
            "driver-test",
            "Testtechnieker",
            true,
            "Continu");
    }
}

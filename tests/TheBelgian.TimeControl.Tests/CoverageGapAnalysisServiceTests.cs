using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class CoverageGapAnalysisServiceTests
{
    [Fact]
    public void Analyze_GroupsUnreliableByPlenionAndStop_AndProjectsAliasGain()
    {
        var technician = new Technician
        {
            ExternalId = "10",
            Code = "FDE",
            Name = "Filip Dekuyper",
            Kind = 1,
        };
        var stop = new PilotStop(
            "stop-1",
            new DateOnly(2026, 7, 23),
            "in",
            "out",
            DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T09:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            60,
            "Parking Hofstraat 7",
            "8400",
            "Oostende",
            "Hofstraat 7",
            null,
            null,
            51.23m,
            2.91m,
            "2-JPF-194",
            "19725",
            "Filip Dekuyper",
            true,
            "ok");
        var candidate = new PilotLocationCandidateScore(
            stop,
            90,
            PilotDistanceClassification.StrongLocationMatch,
            40,
            0,
            0,
            10,
            40,
            30,
            80,
            PilotLocationResolutionStatus.NoReliableMatch,
            "parking");
        var unreliable = Resolution(
            1,
            "L1",
            "Kapucijnenstraat 52",
            PilotLocationResolutionStatus.NoReliableMatch,
            [candidate]);
        var unreliableSame = Resolution(
            2,
            "L1",
            "Kapucijnenstraat 52",
            PilotLocationResolutionStatus.ManualReviewRequired,
            [candidate]);
        var reliable = Resolution(
            3,
            "L2",
            "Andere straat 1",
            PilotLocationResolutionStatus.ConfirmedLocationMatch,
            [candidate]);
        var noStop = Resolution(
            4,
            "L3",
            "Zonder stop 9",
            PilotLocationResolutionStatus.NoReliableMatch,
            []);

        var broader = new BroaderValidationResult
        {
            FromDate = new DateOnly(2026, 7, 1),
            ThroughDate = new DateOnly(2026, 7, 28),
            Technicians =
            [
                new BroaderValidationTechnicianResult
                {
                    Query = "Filip Dekuyper",
                    Processed = true,
                    Technician = technician,
                    DriverId = "19725",
                    DriverName = "Filip Dekuyper",
                    Days =
                    [
                        new BroaderValidationDayResult(
                            new DateOnly(2026, 7, 23),
                            "Filip Dekuyper",
                            "19725",
                            "Filip Dekuyper",
                            [new BroaderValidationVehicleContext("1", "FDE", "2-JPF-194")],
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            false,
                            false,
                            0,
                            false,
                            false,
                            false,
                            false,
                            3,
                            0,
                            1,
                            0,
                            0,
                            2,
                            0,
                            0,
                            "Goed",
                            "Werkdag",
                            "test")
                    ],
                    Issues = [],
                    PilotResult = new ReadOnlyPilotResult
                    {
                        Technician = technician,
                        FromDate = new DateOnly(2026, 7, 23),
                        ThroughDate = new DateOnly(2026, 7, 23),
                        RawPlenionRecords = [],
                        RawPowerfleetRecords = [],
                        PlenionRecords = [],
                        PowerfleetRecords = [],
                        PowerfleetStops = [],
                        PerformanceStopMatches = [],
                        DayComparisons = [],
                        Issues = [],
                        SourceObservations = [],
                        PowerfleetFilterSummary = "test",
                        LocationResolutions = [unreliable, unreliableSame, reliable, noStop],
                        GeocodingProvider = "Geoapify",
                    },
                }
            ],
            Summary = new BroaderValidationSummary
            {
                RecurringAddressProblems = [],
                SkippedTechnicians = [],
            },
            Observations = [],
        };

        var analysis = CoverageGapAnalysisService.Analyze(broader);

        Assert.Single(analysis.EmployeeLinks);
        Assert.Equal("19725", analysis.EmployeeLinks[0].PowerfleetDriverId);
        Assert.Equal(["FDE"], analysis.EmployeeLinks[0].InformativeObjectNames);
        Assert.Equal(4, analysis.MatchBreakdown.TotalLocationResolutions);
        Assert.Equal(1, analysis.MatchBreakdown.ReliableCount);
        Assert.Equal(25, analysis.MatchBreakdown.ReliablePercent);
        Assert.Equal(2, analysis.AliasProjection.UniqueProblemLocations);
        Assert.Equal(2, analysis.AliasProjection.PerformancesFlippedIfAllAliasesConfirmed);
        Assert.Equal(75, analysis.AliasProjection.PotentialReliablePercentAfterAliasConfirmation);
        Assert.Contains("KnownLocationAlias", analysis.AliasTableAdvice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(90.0, true)]
    [InlineData(500.0, true)]
    [InlineData(501.0, false)]
    public void IsConfirmableAlias_RequiresNearbyStop(double meters, bool expected)
    {
        var candidate = new PilotLocationCandidateScore(
            new PilotStop(
                "s",
                new DateOnly(2026, 7, 23),
                "a",
                "b",
                DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-07-23T09:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
                60,
                "x",
                null,
                null,
                null,
                null,
                null,
                51m,
                3m,
                null,
                "1",
                "n",
                true,
                "ok"),
            meters,
            PilotDistanceClassification.PossibleLocationMatch,
            10,
            0,
            0,
            0,
            0,
            0,
            0,
            PilotLocationResolutionStatus.NoReliableMatch,
            "t");

        Assert.Equal(expected, CoverageGapAnalysisService.IsConfirmableAlias(candidate));
    }

    [Fact]
    public void IsConfirmableAlias_ReturnsFalseWithoutCandidate()
    {
        Assert.False(CoverageGapAnalysisService.IsConfirmableAlias(null));
    }

    private static PilotLocationResolution Resolution(
        long id,
        string lacleunik,
        string address,
        PilotLocationResolutionStatus status,
        IReadOnlyList<PilotLocationCandidateScore> candidates) =>
        new(
            id,
            new DateOnly(2026, 7, 23),
            "P",
            "Project",
            "B1",
            DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T09:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            lacleunik,
            address,
            address.ToLowerInvariant(),
            "hash-" + lacleunik,
            new GeocodingResult(
                GeocodingStatus.Geocoded,
                "Geoapify",
                new GeocodingCandidate(
                    new TheBelgian.TimeControl.Core.Interfaces.GeoCoordinate(51.2, 2.9),
                    address,
                    "high",
                    null,
                    []),
                []),
            candidates,
            status,
            "test",
            "test");
}

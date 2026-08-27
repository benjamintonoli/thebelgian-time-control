using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class PerformanceActivityClassifierTests
{
    [Theory]
    [InlineData("Verplaatsing naar klant", PerformanceActivityType.Travel)]
    [InlineData("Middagpauze", PerformanceActivityType.Break)]
    [InlineData("Jaarlijks verlof", PerformanceActivityType.Absence)]
    [InlineData("Thuiswerk administratie", PerformanceActivityType.RemoteWork)]
    [InlineData("Administratie planning", PerformanceActivityType.Administration)]
    [InlineData("Werk op kantoor", PerformanceActivityType.OfficeWork)]
    [InlineData("Montage op werf", PerformanceActivityType.SiteWork)]
    [InlineData("Onderhoud brandmelders", PerformanceActivityType.CustomerWork)]
    [InlineData("Opleiding nieuwe procedure", PerformanceActivityType.OtherNonLocationBound)]
    [InlineData("XYZ onbekend", PerformanceActivityType.Unknown)]
    public void Classify_UsesDescriptionMarkers(
        string description,
        PerformanceActivityType expected)
    {
        var performance = Performance(description, deliveryAddressId: null);
        var result = PerformanceActivityClassifier.Classify(performance, "Tech", null);
        Assert.Equal(expected, result.ActivityType);
    }

    [Fact]
    public void Classify_CustomerAddressWithoutMarker_BecomesCustomerWork()
    {
        var performance = Performance("Uitvoering", deliveryAddressId: "123");
        var result = PerformanceActivityClassifier.Classify(performance, "Tech", null);
        Assert.Equal(PerformanceActivityType.CustomerWork, result.ActivityType);
        Assert.True(result.RequiresGeographicMatch);
    }

    [Fact]
    public void Classify_TravelInLocationDenominator_IsIncorrect()
    {
        var performance = Performance("Verplaatsing", deliveryAddressId: "1");
        var resolution = new PilotLocationResolution(
            performance.ExternalId,
            performance.Date,
            null,
            null,
            null,
            performance.StartDateTime,
            performance.EndDateTime,
            "1",
            "x",
            "x",
            "h",
            new GeocodingResult(GeocodingStatus.Geocoded, "Geoapify", null, []),
            [],
            PilotLocationResolutionStatus.NoReliableMatch,
            "t",
            "t");
        var result = PerformanceActivityClassifier.Classify(performance, "Tech", resolution);
        Assert.Equal(PerformanceActivityType.Travel, result.ActivityType);
        Assert.True(result.IncorrectlyInLocationDenominator);
        Assert.False(result.RequiresGeographicMatch);
    }

    [Theory]
    [InlineData("5", "Onderhoud 26500697", PerformanceActivityType.Travel)]
    [InlineData("23", "Montagebon I.R.6300156", PerformanceActivityType.WaitingTime)]
    [InlineData("9", "Montagebon I.R.6300155", PerformanceActivityType.SiteWork)]
    public void Classify_VerifiedMainTaskSemantics_PrecedeProjectAndAddressMarkers(
        string mainTaskExternalId,
        string description,
        PerformanceActivityType expected)
    {
        var performance = Performance(
            description,
            deliveryAddressId: "site",
            mainTaskExternalId: mainTaskExternalId);

        var result = PerformanceActivityClassifier.Classify(performance, "Tech", null);

        Assert.Equal(expected, result.ActivityType);
        Assert.Equal(
            expected is PerformanceActivityType.CustomerWork or PerformanceActivityType.SiteWork,
            result.RequiresGeographicMatch);
    }

    [Fact]
    public void Analyze_RecalculatesMatchRateForLocationBoundOnly()
    {
        var technician = new Technician
        {
            ExternalId = "1",
            Code = "T",
            Name = "Tech",
            Kind = 1,
        };
        var travel = Performance("Verplaatsing", null, 1);
        var customerReliable = Performance("Onderhoud", "10", 2);
        var customerOpen = Performance("Onderhoud", "11", 3);
        var stop = new PilotStop(
            "s",
            new DateOnly(2026, 7, 23),
            "a",
            "b",
            DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T09:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            60,
            "Near",
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
            "ok");
        var nearCandidate = new PilotLocationCandidateScore(
            stop,
            40,
            PilotDistanceClassification.StrongLocationMatch,
            30,
            0,
            0,
            20,
            40,
            30,
            90,
            PilotLocationResolutionStatus.ConfirmedLocationMatch,
            "ok");
        var reliableResolution = Resolution(
            customerReliable,
            PilotLocationResolutionStatus.ConfirmedLocationMatch,
            [nearCandidate]);
        var openFar = Resolution(
            customerOpen,
            PilotLocationResolutionStatus.NoReliableMatch,
            [
                nearCandidate with
                {
                    DistanceMeters = 9000,
                    DistanceClassification = PilotDistanceClassification.LocationMismatch,
                    MatchStatus = PilotLocationResolutionStatus.NoReliableMatch,
                }
            ]);
        var travelResolution = Resolution(
            travel,
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
                    Query = "Tech",
                    Processed = true,
                    Technician = technician,
                    DriverId = "1",
                    DriverName = "Tech",
                    Days = [],
                    Issues = [],
                    PilotResult = new ReadOnlyPilotResult
                    {
                        Technician = technician,
                        FromDate = new DateOnly(2026, 7, 23),
                        ThroughDate = new DateOnly(2026, 7, 23),
                        RawPlenionRecords = [],
                        RawPowerfleetRecords = [],
                        PlenionRecords = [travel, customerReliable, customerOpen],
                        PowerfleetRecords = [],
                        PowerfleetStops = [],
                        PerformanceStopMatches = [],
                        DayComparisons = [],
                        Issues = [],
                        SourceObservations = [],
                        PowerfleetFilterSummary = "t",
                        LocationResolutions =
                        [
                            travelResolution,
                            reliableResolution,
                            openFar
                        ],
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

        var analysis = ActivityClassificationAnalysisService.Analyze(broader);

        Assert.Equal(1, analysis.OpenCases.NotLocationBoundCount);
        Assert.Equal(1, analysis.OpenCases.StillLocationBoundCount);
        Assert.Equal(2, analysis.CorrectedMatch.LocationBoundResolutionCount);
        Assert.Equal(1, analysis.CorrectedMatch.ReliableLocationBoundCount);
        Assert.Equal(50, analysis.CorrectedMatch.CorrectedReliablePercent);
        Assert.Equal(1, analysis.CorrectedMatch.RemainingNoReliableMatchCount);
    }

    private static NormalizedPilotPerformance Performance(
        string description,
        string? deliveryAddressId,
        long id = 1,
        string mainTaskExternalId = "t1") =>
        new(
            id,
            "1",
            new DateOnly(2026, 7, 23),
            DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T09:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            0,
            60,
            60,
            0m,
            "p",
            mainTaskExternalId,
            "b1",
            description,
            null,
            "P1",
            "Project",
            deliveryAddressId,
            deliveryAddressId is null ? null : "Klant",
            deliveryAddressId is null ? null : "Straat 1",
            null,
            null,
            null,
            1,
            1,
            1,
            "ok",
            "ok");

    private static PilotLocationResolution Resolution(
        NormalizedPilotPerformance performance,
        PilotLocationResolutionStatus status,
        IReadOnlyList<PilotLocationCandidateScore> candidates) =>
        new(
            performance.ExternalId,
            performance.Date,
            performance.ProjectNumber,
            performance.ProjectName,
            performance.WorkOrderNumber,
            performance.StartDateTime,
            performance.EndDateTime,
            performance.DeliveryAddressExternalId,
            "addr",
            "addr",
            "hash",
            new GeocodingResult(GeocodingStatus.Geocoded, "Geoapify", null, []),
            candidates,
            status,
            "diag",
            "assess");
}

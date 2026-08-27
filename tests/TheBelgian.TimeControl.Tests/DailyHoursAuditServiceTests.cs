using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Tests;

public sealed class DailyHoursAuditServiceTests
{
    [Fact]
    public void WeekendDates_AreRecognizableBeforeMatching()
    {
        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2026, 7, 4).DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, new DateOnly(2026, 7, 5).DayOfWeek);
    }

    [Fact]
    public void SamePerformanceWithoutAddress_RemainsItsOwnBoundaryBlock()
    {
        var date = new DateOnly(2026, 7, 1);
        var performance = new NormalizedPilotPerformance(
            42, "resource", date, At(date, 8, 0), At(date, 9, 0), 0, 60, 60, 0,
            "project", "9", "bon", "Klantwerk", null, "project", "Project", null,
            null, null, null, null, null, 0, 0, 0, "ok", "ok");

        Assert.True(DailyHoursAuditService.SameSite(performance, performance));
    }

    [Fact]
    public void FirstAndLast_SelectDifferentOrderedVisitsAtSamePhysicalSite()
    {
        var date = new DateOnly(2026, 7, 7);
        var start = At(date, 8, 0);
        var end = At(date, 16, 10);
        var stops = new[]
        {
            Stop("first", date, At(date, 8, 2), At(date, 10, 28), true),
            Stop("last", date, At(date, 11, 7), At(date, 16, 9), true),
        };
        var options = new AdaptiveLocationMatchingOptions();
        var distance = new HaversineDistanceCalculator();
        var resolution = Resolution(date, start, end);

        var first = DailyBoundarySelector.Select(
            DailyBoundarySide.First, start, end, resolution, stops, null, options, distance);
        var last = DailyBoundarySelector.Select(
            DailyBoundarySide.Last, start, end, resolution, stops, null, options, distance);

        Assert.True(first.IsReliable);
        Assert.True(last.IsReliable);
        Assert.Equal("first", first.Selected!.Stop.MergedStopId);
        Assert.Equal("last", last.Selected!.Stop.MergedStopId);
    }

    [Fact]
    public void BoundaryCandidate_DoesNotRequireLocationContinuity()
    {
        var date = new DateOnly(2026, 7, 8);
        var start = At(date, 7, 0);
        var end = At(date, 15, 30);
        var selection = DailyBoundarySelector.Select(
            DailyBoundarySide.First,
            start,
            end,
            Resolution(date, start, end),
            [Stop("no-continuity", date, At(date, 7, 5), At(date, 15, 32), false)],
            null,
            new AdaptiveLocationMatchingOptions(),
            new HaversineDistanceCalculator());

        Assert.True(selection.IsReliable);
        Assert.Equal("no-continuity", selection.Selected!.Stop.MergedStopId);
    }

    [Fact]
    public void BoundaryCandidate_RejectsPassThroughAndStopsBeyondFiveHundredMeters()
    {
        var date = new DateOnly(2026, 7, 8);
        var start = At(date, 7, 0);
        var end = At(date, 15, 30);
        var stops = new[]
        {
            Stop("pass", date, At(date, 7, 5), At(date, 7, 6), false),
            Stop("far", date, At(date, 8, 0), At(date, 12, 0), true, 51.01m),
        };
        var selection = DailyBoundarySelector.Select(
            DailyBoundarySide.First,
            start,
            end,
            Resolution(date, start, end),
            stops,
            null,
            new AdaptiveLocationMatchingOptions(),
            new HaversineDistanceCalculator());

        Assert.False(selection.IsReliable);
        Assert.Null(selection.Selected);
    }

    [Theory]
    [InlineData(6, 0)]
    [InlineData(7, 25)]
    public void Joris29July_TravelDurationDoesNotInfluenceFirstLocationBoundary(
        int travelStartHour,
        int travelStartMinute)
    {
        var date = new DateOnly(2026, 7, 29);
        var travel = Performance(280570, date, travelStartHour, travelStartMinute, 7, 40, "5", "Verplaatsingen");
        var customer = Performance(280569, date, 7, 40, 11, 50, "9", "Onderhoud 26500697");

        var jobs = DailyHoursAuditService.SelectLocationJobs([travel, customer], "Joris Rottiers", []);

        Assert.Single(jobs);
        Assert.Equal(280569, jobs[0].ExternalId);
        Assert.Equal(At(date, 7, 40), jobs[0].StartDateTime);
    }

    [Fact]
    public void WaitingTimeCannotBecomeFirstOrLastBoundary()
    {
        var date = new DateOnly(2026, 7, 23);
        var customer = Performance(280252, date, 8, 30, 17, 0, "9", "Onderhoud");
        var waiting = Performance(280182, date, 20, 0, 20, 25, "23", "Werkuren Wachtdienst");

        var jobs = DailyHoursAuditService.SelectLocationJobs([customer, waiting], "Shane Van Geldorp", []);

        Assert.Single(jobs);
        Assert.Equal(customer.ExternalId, jobs[0].ExternalId);
    }

    [Fact]
    public void Garrit06July_FirstUsesImmediateSameCustomerContextWithinFiveMinutes()
    {
        var date = new DateOnly(2026, 7, 6);
        var performance = Performance(279257, date, 7, 15, 9, 45, "9", "Onderhoud 26501953");
        var context = Stop("context", date, At(date, 7, 16), At(date, 7, 28), true, 51.01m);
        var exactStop = Stop("exact", date, At(date, 7, 44), At(date, 9, 45), true);
        var exact = DailyBoundarySelector.Select(
            DailyBoundarySide.First,
            performance.StartDateTime,
            performance.EndDateTime,
            Resolution(date, performance.StartDateTime, performance.EndDateTime),
            [context, exactStop], null, new AdaptiveLocationMatchingOptions(), new HaversineDistanceCalculator());

        var evidence = DailyBoundaryContextSelector.Select(
            DailyBoundarySide.First,
            new BoundaryBlock(performance.StartDateTime, performance.EndDateTime, [performance.ExternalId]),
            performance,
            exact,
            [context, exactStop],
            ContextIndex(new GeoCoordinate(51.01, 3), "customer"),
            3,
            new HaversineDistanceCalculator());

        Assert.Equal(DailyBoundaryEvidenceType.ContextSupported, evidence.EvidenceType);
        Assert.Equal(At(date, 7, 16), evidence.EffectiveBoundaryTime);
        Assert.Equal(1, evidence.EffectiveDeviationMinutes);
        Assert.Equal(29, evidence.RawExactSiteDeviationMinutes);
    }

    [Fact]
    public void OtherKnownWorkLocationCannotSupportBoundary()
    {
        var evidence = ContextEvidence(contextCustomer: "other", contextLatitude: 51.01, contextMinute: 16);

        Assert.Equal(DailyBoundaryEvidenceType.ExactSite, evidence.EvidenceType);
        Assert.Equal(29, evidence.EffectiveDeviationMinutes);
    }

    [Fact]
    public void SameCustomerContextBeyondHundredMetersCannotSupportBoundary()
    {
        var evidence = ContextEvidence(contextCustomer: "customer", contextLatitude: 51.0111, contextMinute: 16);

        Assert.Equal(DailyBoundaryEvidenceType.ExactSite, evidence.EvidenceType);
    }

    [Fact]
    public void SameCustomerContextMoreThanFiveMinutesFromBoundaryNeedsReview()
    {
        var evidence = ContextEvidence(contextCustomer: "customer", contextLatitude: 51.01, contextMinute: 21);

        Assert.Equal(DailyBoundaryEvidenceType.Review, evidence.EvidenceType);
        Assert.False(evidence.IsReliable);
        Assert.Null(evidence.EffectiveDeviationMinutes);
        Assert.Equal(29, evidence.PotentialDeviationMinutes);
    }

    [Theory]
    [InlineData(
        "Sint-Pieterskerklaan 58 - Laconiastraat 16, 8000, SINT PIETERS, BE",
        "Sint-Pieterskerklaan 58, 8000 Brugge, België")]
    [InlineData(
        "Grote Baan 111, 9100, Sint-Niklaas, BE",
        "Grote Baan 111, 9100 Sint-Niklaas, België")]
    public void ContextAddressMatching_RecognizesStrongCompositeAddressWithoutGeocoding(
        string plenion,
        string powerfleet)
    {
        Assert.True(DailyBoundaryContextIndexProvider.StrongAddressEquivalent(plenion, powerfleet));
    }

    private static DailyBoundaryEvidence ContextEvidence(
        string contextCustomer,
        double contextLatitude,
        int contextMinute)
    {
        var date = new DateOnly(2026, 7, 6);
        var performance = Performance(279257, date, 7, 15, 9, 45, "9", "Onderhoud");
        var context = Stop("context", date, At(date, 7, contextMinute), At(date, 7, 28), true, 51.01m);
        var exactStop = Stop("exact", date, At(date, 7, 44), At(date, 9, 45), true);
        var exact = DailyBoundarySelector.Select(
            DailyBoundarySide.First, performance.StartDateTime, performance.EndDateTime,
            Resolution(date, performance.StartDateTime, performance.EndDateTime),
            [context, exactStop], null, new AdaptiveLocationMatchingOptions(), new HaversineDistanceCalculator());
        return DailyBoundaryContextSelector.Select(
            DailyBoundarySide.First,
            new BoundaryBlock(performance.StartDateTime, performance.EndDateTime, [performance.ExternalId]),
            performance,
            exact,
            [context, exactStop],
            ContextIndex(new GeoCoordinate(contextLatitude, 3), contextCustomer),
            3,
            new HaversineDistanceCalculator());
    }

    private static DailyBoundaryContextIndex ContextIndex(GeoCoordinate coordinate, string customer) =>
        new(
            [new DailyBoundaryContextLocation("site", "Known site", "Known address", coordinate, customer)],
            [new PlenionWorkOrder
            {
                ExternalId = "order",
                Number = "bon",
                CustomerExternalId = "customer",
                ProjectExternalId = "project",
                DeliveryAddressExternalId = "official",
            }],
            []);

    private static NormalizedPilotPerformance Performance(
        long id,
        DateOnly date,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        string mainTask,
        string description) =>
        new(
            id, "resource", date, At(date, startHour, startMinute), At(date, endHour, endMinute),
            0, 0, 0, 0, "project", mainTask, "bon", description, null, "project", "Project", "official",
            "Customer", "Street", "1000", "City", "BE", 1, 1, 1, "ok", "ok");

    private static PilotLocationResolution Resolution(
        DateOnly date,
        DateTimeOffset start,
        DateTimeOffset end) =>
        new(
            1, date, null, null, null, start, end, "site", "address", "address", "hash",
            new GeocodingResult(
                GeocodingStatus.Geocoded,
                "test",
                new GeocodingCandidate(new(51, 3), "address", "high", "building", []),
                []),
            [], PilotLocationResolutionStatus.ConfirmedLocationMatch, "test", "test");

    private static PilotStop Stop(
        string id,
        DateOnly date,
        DateTimeOffset arrival,
        DateTimeOffset departure,
        bool continuity,
        decimal latitude = 51m) =>
        new(
            id, date, "in", "out", arrival, departure,
            (int)Math.Round((departure - arrival).TotalMinutes),
            "address", null, null, null, null, null,
            latitude, 3m, null, "driver", "Driver", continuity, "test");

    private static DateTimeOffset At(DateOnly date, int hour, int minute) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, TimeSpan.FromHours(2));
}

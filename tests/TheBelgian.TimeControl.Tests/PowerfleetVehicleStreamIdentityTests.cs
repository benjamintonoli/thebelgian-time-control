using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class PowerfleetVehicleStreamIdentityTests
{
    [Fact]
    public void OneTechnicianOneVehicle_IsOneStableStream()
    {
        var date = new DateOnly(2026, 7, 1);
        var risk = PowerfleetVehicleStreamIdentity.Analyze(date, "Technieker",
            [Trip("a", date, 8, 9, "object-1"), Trip("b", date, 10, 11, "object-1")]);

        Assert.Equal(1, risk.PhysicalStreamCount);
        Assert.Equal("SeparatedVehicleStreams", risk.Status);
        Assert.Equal(0, risk.OverlapMinutes);
    }

    [Fact]
    public void HistoricalVehicleSwitchWithoutOverlap_RemainsSeparatedButNotAmbiguous()
    {
        var date = new DateOnly(2026, 7, 2);
        var trips = new[]
        {
            Trip("a1", date, 7, 8, "old"), Trip("a2", date, 9, 10, "old"),
            Trip("b1", date, 12, 13, "new"), Trip("b2", date, 14, 15, "new"),
        };

        var risk = PowerfleetVehicleStreamIdentity.Analyze(date, "Technieker", trips);
        var stops = PilotLocationMatcher.ReconstructStops(trips, []);

        Assert.Equal(2, risk.PhysicalStreamCount);
        Assert.Equal("SeparatedVehicleStreams", risk.Status);
        Assert.Equal(2, stops.Length);
        Assert.All(stops, stop => Assert.Equal(stop.IncomingTripId[..1], stop.OutgoingTripId[..1]));
    }

    [Fact]
    public void ConcurrentPhysicalStreams_AreAmbiguousAndNeverCrossMerged()
    {
        var date = new DateOnly(2026, 7, 3);
        var trips = new[]
        {
            Trip("a1", date, 8, 9, "object-a"), Trip("a2", date, 16, 17, "object-a"),
            Trip("b1", date, 8, 10, "object-b"), Trip("b2", date, 15, 18, "object-b"),
        };

        var risk = PowerfleetVehicleStreamIdentity.Analyze(date, "Technieker", trips);
        var stops = PilotLocationMatcher.ReconstructStops(trips, []);

        Assert.Equal(PowerfleetVehicleStreamIdentity.AmbiguousStatus, risk.Status);
        Assert.True(risk.OverlapMinutes > 0);
        Assert.Equal(2, stops.Length);
        Assert.Contains(stops, stop => stop.ObjectId == "object-a" && stop.IncomingTripId == "a1" && stop.OutgoingTripId == "a2");
        Assert.Contains(stops, stop => stop.ObjectId == "object-b" && stop.IncomingTripId == "b1" && stop.OutgoingTripId == "b2");
    }

    [Fact]
    public void InterleavedTripsWithoutSimultaneousDriving_AreStillConcurrentStreams()
    {
        var date = new DateOnly(2026, 7, 30);
        var trips = new[]
        {
            Trip("a1", date, 6, 7, "object-a"), Trip("a2", date, 20, 22, "object-a"),
            Trip("b1", date, 8, 9, "object-b"), Trip("b2", date, 22, 23, "object-b"),
        };

        var risk = PowerfleetVehicleStreamIdentity.Analyze(date, "Technieker", trips);

        Assert.Equal(PowerfleetVehicleStreamIdentity.AmbiguousStatus, risk.Status);
        Assert.Equal(14 * 60, risk.OverlapMinutes);
    }

    [Fact]
    public void AmbiguousAssignment_RemovesFictiveBoundarySelection()
    {
        var date = new DateOnly(2026, 7, 3);
        var stop = new MergedPilotStop("visit", date, At(date, 8), At(date, 16), 480,
            "site", 51m, 4m, "driver", "Driver", ["stop"], false, "object-a", "1-ABC-123");
        var candidate = new DailyBoundaryCandidate(stop, 10, AdaptiveDistanceZone.Strong0To100,
            480, true, 95, "spatial");
        var selection = new DailyBoundarySelection(DailyBoundarySide.Last,
            AdaptiveMatchDecision.Confirmed, candidate, [candidate], "selected", null);
        var risk = new PowerfleetVehicleStreamRisk(date, "Technieker", 2,
            ["object:a", "object:b"], 60, PowerfleetVehicleStreamIdentity.AmbiguousStatus, "overlap");

        var result = DailyHoursAuditService.AmbiguousVehicleSelection(selection, risk);

        Assert.Equal(AdaptiveMatchDecision.Ambiguous, result.Decision);
        Assert.Null(result.Selected);
        Assert.Contains(PowerfleetVehicleStreamIdentity.AmbiguousStatus, result.Assessment);
    }

    [Fact]
    public void Rajco23July_AartselaarStreamDepartsAt1649And1544StreamCannotCloseIt()
    {
        var date = new DateOnly(2026, 7, 23);
        var trips = new[]
        {
            Trip("site-in", date, 8, 8, "aartselaar", startMinute: 25, endMinute: 49,
                endAddress: "Antwerpsesteenweg 136"),
            Trip("site-out", date, 16, 17, "aartselaar", startMinute: 49, endMinute: 15,
                startAddress: "Antwerpsesteenweg 136"),
            Trip("other-in", date, 8, 9, "elsene", startMinute: 20, endMinute: 3,
                endAddress: "Limaugestraat"),
            Trip("other-out", date, 15, 16, "elsene", startMinute: 44, endMinute: 29,
                startAddress: "Limaugestraat"),
        };

        var stops = PilotLocationMatcher.ReconstructStops(trips, []);
        var site = Assert.Single(stops.Where(stop => stop.ObjectId == "aartselaar"));
        var risk = PowerfleetVehicleStreamIdentity.Analyze(date, "Rajco Cools", trips);

        Assert.Equal(At(date, 16, 49), site.Departure);
        Assert.Equal("site-in", site.IncomingTripId);
        Assert.Equal("site-out", site.OutgoingTripId);
        Assert.DoesNotContain(stops, stop => stop.IncomingTripId == "site-in" && stop.OutgoingTripId == "other-out");
        Assert.Equal(PowerfleetVehicleStreamIdentity.AmbiguousStatus, risk.Status);
    }

    [Fact]
    public void VisitCandidatesNeverMergeDifferentPhysicalObjects()
    {
        var date = new DateOnly(2026, 7, 4);
        var left = Stop("left", date, 8, 10, "object-a");
        var right = Stop("right", date, 10, 12, "object-b");

        var visits = VisitCandidateBuilder.Build([left, right], new(),
            new TheBelgian.TimeControl.Core.Services.HaversineDistanceCalculator(), false);

        Assert.Equal(2, visits.Count);
    }

    private static NormalizedPilotTrip Trip(
        string id,
        DateOnly date,
        int startHour,
        int endHour,
        string objectId,
        int startMinute = 0,
        int endMinute = 0,
        string? startAddress = "from",
        string? endAddress = "to") =>
        new(id, At(date, startHour, startMinute), At(date, endHour, endMinute), 30, null, 10,
            "driver-1", "Technieker", objectId, "Vehicle display", "1-ABC-123",
            startAddress, startAddress, null, null, endAddress, endAddress, null, null,
            51m, 4m, 51m, 4m, "test");

    private static PilotStop Stop(
        string id, DateOnly date, int arrivalHour, int departureHour, string objectId) =>
        new(id, date, "in-" + id, "out-" + id, At(date, arrivalHour), At(date, departureHour),
            120, "site", null, null, null, null, null, 51m, 4m, "1-ABC-123", "driver-1",
            "Technieker", true, "test", objectId, "Vehicle display");

    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, TimeSpan.FromHours(2));
}

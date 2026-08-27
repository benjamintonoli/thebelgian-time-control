using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class WorksiteSessionDetectorTests
{
    [Fact]
    public void SimpleSiteWithoutInternalMovement_RemainsExactVisit()
    {
        var date = new DateOnly(2026, 7, 1);
        var block = Block(date, 8, 0, 16, 0, 1);
        var stop = Stop("site", date, "inbound", "outbound", At(date, 8, 1), At(date, 15, 59));

        var result = Detect(DailyBoundarySide.Last, block, [Job(1, block)], [stop],
            [Trip("inbound", date, 7, 0, 8, 1, 40), Trip("outbound", date, 15, 59, 17, 0, 40)],
            stop);

        Assert.False(result.Changed);
        Assert.Equal("site", result.Selection.Selected!.Stop.MergedStopId);
    }

    [Fact]
    public void ShortInternalTrip_ExtendsDepartureToEndOfContinuousPresence()
    {
        var date = new DateOnly(2026, 7, 2);
        var block = Block(date, 8, 0, 16, 0, 1);
        var first = Stop("a", date, "inbound", "local", At(date, 8, 1), At(date, 9, 0));
        var last = Stop("b", date, "local", "outbound", At(date, 9, 4), At(date, 15, 59));

        var result = Detect(DailyBoundarySide.Last, block, [Job(1, block)], [first, last],
            [Trip("inbound", date, 7, 0, 8, 1, 30), Trip("local", date, 9, 0, 9, 4, 0.6m),
             Trip("outbound", date, 15, 59, 17, 0, 30)],
            first);

        Assert.True(result.Changed);
        Assert.Equal(At(date, 15, 59), result.Selection.Selected!.Stop.Departure);
        Assert.Equal(2, result.Selection.WorksiteSession!.StopIds.Count);
    }

    [Fact]
    public void MultipleStopClusters_FormOneSessionThroughLocalTrips()
    {
        var scenario = LocalChain(threeStops: true, localKilometres: 0.5m);

        var result = Detect(DailyBoundarySide.Last, scenario.Block, scenario.Jobs,
            scenario.Stops, scenario.Trips, scenario.Stops[0]);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Selection.WorksiteSession!.StopIds.Count);
        Assert.Equal(scenario.Block.End.AddMinutes(-1),
            result.Selection.WorksiteSession.DepartureTime);
    }

    [Fact]
    public void MultiKilometreIndustrialMovement_CanRemainOneSession()
    {
        var scenario = LocalChain(threeStops: false, localKilometres: 4m);

        var result = Detect(DailyBoundarySide.Last, scenario.Block, scenario.Jobs,
            scenario.Stops, scenario.Trips, scenario.Stops[0]);

        Assert.True(result.Changed);
        Assert.Equal(scenario.Block.End.AddMinutes(-1),
            result.Selection.WorksiteSession!.DepartureTime);
    }

    [Fact]
    public void NearbyOtherCustomerWithoutClearOutboundEvidence_IsNotAutoMerged()
    {
        var date = new DateOnly(2026, 7, 5);
        var block = Block(date, 8, 0, 16, 0, 1);
        var site = Stop("site", date, "in", "nearby", At(date, 8, 0), At(date, 15, 20));
        var other = Stop("other", date, "nearby", "later", At(date, 15, 24), At(date, 16, 0));

        var result = Detect(DailyBoundarySide.Last, block, [Job(1, block)], [site, other],
            [Trip("in", date, 7, 0, 8, 0, 20), Trip("nearby", date, 15, 20, 15, 24, 0.4m),
             Trip("later", date, 16, 0, 16, 5, 0.4m)],
            site);

        Assert.False(result.Changed);
        Assert.Equal("site", result.Selection.Selected!.Stop.MergedStopId);
    }

    [Fact]
    public void OtherLocationBoundJob_BreaksContinuity()
    {
        var date = new DateOnly(2026, 7, 6);
        var block = Block(date, 8, 0, 12, 0, 1);
        var site = Stop("site", date, "in", "to-other", At(date, 8, 0), At(date, 11, 40));
        var other = Stop("other", date, "to-other", "out", At(date, 11, 45), At(date, 12, 0));
        var secondJob = Job(2, Block(date, 11, 45, 14, 0, 2));

        var result = Detect(DailyBoundarySide.Last, block, [Job(1, block), secondJob], [site, other],
            [Trip("in", date, 7, 0, 8, 0, 20), Trip("to-other", date, 11, 40, 11, 45, 0.5m),
             Trip("out", date, 12, 0, 13, 0, 20)],
            site);

        Assert.False(result.Changed);
        Assert.Equal("site", result.Selection.Selected!.Stop.MergedStopId);
    }

    [Fact]
    public void DefinitiveLongOutboundTrip_EndsSession()
    {
        var date = new DateOnly(2026, 7, 7);
        var block = Block(date, 8, 0, 17, 0, 1);
        var site = Stop("site", date, "in", "out", At(date, 8, 0), At(date, 16, 2));
        var home = Stop("home", date, "out", "later", At(date, 17, 0), At(date, 17, 10));

        var result = Detect(DailyBoundarySide.Last, block, [Job(1, block)], [site, home],
            [Trip("in", date, 7, 0, 8, 0, 30), Trip("out", date, 16, 2, 17, 0, 35),
             Trip("later", date, 17, 10, 17, 20, 1)],
            site);

        Assert.False(result.Changed);
        Assert.Equal(At(date, 16, 2), result.Selection.Selected!.Stop.Departure);
    }

    [Fact]
    public void ReturnAfterShortInternalMovement_RemainsSameSession()
    {
        var scenario = LocalChain(threeStops: true, localKilometres: 0.5m);

        var result = Detect(DailyBoundarySide.Last, scenario.Block, scenario.Jobs,
            scenario.Stops, scenario.Trips, scenario.Stops[0]);

        Assert.True(result.Changed);
        Assert.Equal(["a", "b", "a-return"], result.Selection.WorksiteSession!.StopIds);
    }

    [Fact]
    public void Yarne13July_LastIsReconstructedAt1529()
    {
        var date = new DateOnly(2026, 7, 13);
        var block = Block(date, 7, 0, 15, 30, 279737);
        var exact = Stop("294685870/294690161", date, "294685870", "294690161",
            At(date, 7, 1, 35), At(date, 7, 20, 34));
        var continuation = Stop("294690161/294856944", date, "294690161", "294856944",
            At(date, 7, 23, 48), At(date, 15, 29, 22));

        var result = Detect(DailyBoundarySide.Last, block, [Job(279737, block)], [exact, continuation],
            [Trip("294685870", date, 5, 55, 7, 1, 67), Trip("294690161", date, 7, 20, 7, 23, 0.57m),
             Trip("294856944", date, 15, 29, 16, 37, 68)],
            exact);

        Assert.True(result.Changed);
        Assert.Equal(At(date, 15, 29, 22), result.Selection.Selected!.Stop.Departure);
    }

    [Fact]
    public void Ibrahima23July_DoesNotUse0841AsLast()
    {
        var date = new DateOnly(2026, 7, 23);
        var block = Block(date, 8, 0, 16, 30, 280243);
        var exact = Stop("296556705/296577812", date, "296556705", "296577812",
            At(date, 8, 0, 38), At(date, 8, 40, 48));
        var continuation = Stop("296577812/296711562", date, "296577812", "296711562",
            At(date, 8, 48, 36), At(date, 16, 28, 25));

        var result = Detect(DailyBoundarySide.Last, block, [Job(280243, block)], [exact, continuation],
            [Trip("296556705", date, 6, 47, 8, 0, 58), Trip("296577812", date, 8, 40, 8, 48, 0.25m),
             Trip("296711562", date, 16, 28, 17, 34, 43)],
            exact);

        Assert.True(result.Changed);
        Assert.Equal(At(date, 16, 28, 25), result.Selection.Selected!.Stop.Departure);
    }

    [Fact]
    public void Nabil24July_FirstIsReconstructedAt0738()
    {
        var date = new DateOnly(2026, 7, 24);
        var block = Block(date, 7, 40, 11, 30, 280242);
        var inbound = Stop("296759534/296760189", date, "296759534", "296760189",
            At(date, 7, 38, 36), At(date, 7, 39, 24));
        var middle = Stop("296760189/296798390", date, "296760189", "296798390",
            At(date, 7, 41, 11), At(date, 10, 0, 46));
        var exact = Stop("296798390/296845870", date, "296798390", "296845870",
            At(date, 10, 2, 3), At(date, 11, 30, 57));

        var laterJobBlock = Block(date, 11, 30, 15, 10, 280263);
        var result = Detect(DailyBoundarySide.First, block,
            [Job(280242, block), Job(280263, laterJobBlock)], [inbound, middle, exact],
            [Trip("296759534", date, 7, 5, 7, 38, 25), Trip("296760189", date, 7, 39, 7, 41, 0.33m),
             Trip("296798390", date, 10, 0, 10, 2, 0.15m), Trip("296845870", date, 11, 30, 12, 40, 30)],
            exact);

        Assert.True(result.Changed);
        Assert.Equal(At(date, 7, 38, 36), result.Selection.Selected!.Stop.Arrival);
    }

    [Fact]
    public void Bart13July_ValidatedDepartureAt160246RemainsTruePositive()
    {
        var date = new DateOnly(2026, 7, 13);
        var block = Block(date, 8, 30, 17, 0, 280204);
        var earlier = Stop("earlier", date, "in", "local", At(date, 8, 27), At(date, 13, 6));
        var exact = Stop("exact", date, "local", "out", At(date, 14, 47), At(date, 16, 2, 46));

        var result = Detect(DailyBoundarySide.Last, block, [Job(280204, block)], [earlier, exact],
            [Trip("in", date, 7, 33, 8, 27, 40), Trip("local", date, 13, 6, 14, 47, 1),
             Trip("out", date, 16, 2, 17, 6, 30)],
            exact);

        Assert.False(result.Changed);
        Assert.Equal(At(date, 16, 2, 46), result.Selection.Selected!.Stop.Departure);
        Assert.Equal(57, HoursAuditService.PositiveWholeMinutes(
            block.End - result.Selection.Selected.Stop.Departure));
    }

    private static WorksiteSessionDetection Detect(
        DailyBoundarySide side,
        BoundaryBlock block,
        IReadOnlyList<NormalizedPilotPerformance> jobs,
        IReadOnlyList<PilotStop> stops,
        IReadOnlyList<NormalizedPilotTrip> trips,
        PilotStop anchor) =>
        WorksiteSessionDetector.Apply("Technieker", side, block, jobs, stops, trips, Selection(side, anchor));

    private static DailyBoundarySelection Selection(DailyBoundarySide side, PilotStop anchor)
    {
        var merged = new MergedPilotStop(anchor.StopId, anchor.Date, anchor.Arrival, anchor.Departure,
            anchor.DurationMinutes, anchor.Address, anchor.Latitude, anchor.Longitude,
            anchor.DriverId, anchor.DriverName, [anchor.StopId], false);
        var candidate = new DailyBoundaryCandidate(
            merged, 50, AdaptiveDistanceZone.Strong0To100, anchor.DurationMinutes,
            true, 90, "exact");
        return new DailyBoundarySelection(side, AdaptiveMatchDecision.Confirmed,
            candidate, [candidate], "exact", null);
    }

    private static (BoundaryBlock Block, IReadOnlyList<NormalizedPilotPerformance> Jobs,
        IReadOnlyList<PilotStop> Stops, IReadOnlyList<NormalizedPilotTrip> Trips) LocalChain(
        bool threeStops,
        decimal localKilometres)
    {
        var date = new DateOnly(2026, 7, 3);
        var block = Block(date, 8, 0, 16, 0, 1);
        var stops = new List<PilotStop>
        {
            Stop("a", date, "in", "local-1", At(date, 8, 1), At(date, 9, 0)),
            Stop("b", date, "local-1", threeStops ? "local-2" : "out",
                At(date, 9, 5), threeStops ? At(date, 12, 0) : At(date, 15, 59)),
        };
        var trips = new List<NormalizedPilotTrip>
        {
            Trip("in", date, 7, 0, 8, 1, 30),
            Trip("local-1", date, 9, 0, 9, 5, localKilometres),
        };
        if (threeStops)
        {
            stops.Add(Stop("a-return", date, "local-2", "out", At(date, 12, 5), At(date, 15, 59)));
            trips.Add(Trip("local-2", date, 12, 0, 12, 5, localKilometres));
        }
        trips.Add(Trip("out", date, 15, 59, 17, 0, 30));
        return (block, [Job(1, block)], stops, trips);
    }

    private static BoundaryBlock Block(
        DateOnly date, int startHour, int startMinute, int endHour, int endMinute, long id) =>
        new(At(date, startHour, startMinute), At(date, endHour, endMinute), [id]);

    private static NormalizedPilotPerformance Job(long id, BoundaryBlock block) =>
        new(id, "resource", DateOnly.FromDateTime(block.Start.DateTime), block.Start, block.End,
            0, 0, 0, 0, "project", "9", "bon", "werk", null, "project", "Project",
            "site", "Customer", "Street", "1000", "City", "BE", 1, 1, 1, "ok", "ok");

    private static PilotStop Stop(
        string id, DateOnly date, string incoming, string outgoing,
        DateTimeOffset arrival, DateTimeOffset departure) =>
        new(id, date, incoming, outgoing, arrival, departure,
            (int)Math.Round((departure - arrival).TotalMinutes), id, null, null, null, null, null,
            51m, 3m, null, "driver", "Driver", true, "test");

    private static NormalizedPilotTrip Trip(
        string id, DateOnly date, int startHour, int startMinute,
        int endHour, int endMinute, decimal kilometres) =>
        new(id, At(date, startHour, startMinute), At(date, endHour, endMinute),
            Math.Max(1, (int)(At(date, endHour, endMinute) - At(date, startHour, startMinute)).TotalMinutes),
            null, kilometres, "driver", "Driver", null, null, null,
            "from", "from", null, null, "to", "to", null, null,
            51m, 3m, 51.001m, 3.001m, "test");

    private static DateTimeOffset At(DateOnly date, int hour, int minute, int second = 0) =>
        new(date.Year, date.Month, date.Day, hour, minute, second, TimeSpan.FromHours(2));
}

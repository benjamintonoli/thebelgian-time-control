using System.Diagnostics;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal enum WorksiteSessionConfidence { Strong, Ambiguous }

internal sealed record WorksiteSession(
    string SessionId,
    string Technician,
    DateOnly Date,
    IReadOnlyList<long> PerformanceIds,
    DateTimeOffset ArrivalTime,
    DateTimeOffset DepartureTime,
    IReadOnlyList<string> StopIds,
    IReadOnlyList<string> TripIds,
    WorksiteSessionConfidence Confidence,
    string Reason);

internal sealed record WorksiteSessionDetection(
    DailyBoundarySelection Selection,
    bool Considered,
    bool Changed,
    bool Ambiguous,
    int ClusterCount,
    int HistoricalLookups,
    TimeSpan Duration);

/// <summary>
/// Reconstructs continuous presence around an already reliable exact-site anchor.
/// It never creates a match from an unanchored GPS cluster.
/// </summary>
internal static class WorksiteSessionDetector
{
    internal const decimal MaximumLocalTripKilometres = 5m;
    internal const int MaximumLocalTripMinutes = 20;
    internal const int BoundaryConfirmationMinutes = 5;
    private static readonly TimeSpan BlockMargin = TimeSpan.FromMinutes(30);

    public static WorksiteSessionDetection Apply(
        string technician,
        DailyBoundarySide side,
        BoundaryBlock block,
        IReadOnlyList<NormalizedPilotPerformance> locationJobs,
        IReadOnlyList<PilotStop> stops,
        IReadOnlyList<NormalizedPilotTrip> trips,
        DailyBoundarySelection exact)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!exact.IsReliable || exact.Selected is null)
        {
            return Result(exact, false, false, false, 0, stopwatch);
        }

        var ordered = stops
            .Where(stop => stop.Date == DateOnly.FromDateTime(block.Start.DateTime) &&
                           stop.Departure >= block.Start - BlockMargin &&
                           stop.Arrival <= block.End + BlockMargin)
            .OrderBy(stop => stop.Arrival)
            .ThenBy(stop => stop.StopId, StringComparer.Ordinal)
            .ToArray();
        var anchorIds = exact.Selected.Stop.SourceStopIds.ToHashSet(StringComparer.Ordinal);
        var anchorIndexes = ordered.Select((stop, index) => (stop, index))
            .Where(item => anchorIds.Contains(item.stop.StopId))
            .Select(item => item.index)
            .ToArray();
        if (anchorIndexes.Length == 0)
        {
            return Result(exact, false, false, false, 0, stopwatch);
        }

        var tripById = trips.ToDictionary(item => item.ExternalId, StringComparer.Ordinal);
        var first = anchorIndexes.Min();
        var last = anchorIndexes.Max();
        while (first > 0 && CanBridge(
                   ordered[first - 1], ordered[first], block, locationJobs, tripById))
        {
            first--;
        }

        while (last + 1 < ordered.Length && CanBridge(
                   ordered[last], ordered[last + 1], block, locationJobs, tripById))
        {
            last++;
        }

        var component = ordered[first..(last + 1)];
        if (component.Length <= anchorIndexes.Length)
        {
            return Result(exact, true, false, false, 1, stopwatch);
        }

        var boundaryTime = side == DailyBoundarySide.First
            ? component[0].Arrival
            : component[^1].Departure;
        var exactTime = side == DailyBoundarySide.First
            ? exact.Selected.Stop.Arrival
            : exact.Selected.Stop.Departure;
        if (boundaryTime == exactTime)
        {
            return Result(exact, true, false, false, 1, stopwatch);
        }

        var plenionTime = side == DailyBoundarySide.First ? block.Start : block.End;
        var closeToRegisteredBoundary = Math.Abs((boundaryTime - plenionTime).TotalMinutes) <=
                                        BoundaryConfirmationMinutes;
        var hasRouteStructure = side == DailyBoundarySide.First
            ? IsClearInbound(component[0], tripById)
            : IsClearOutbound(component[^1], tripById);
        var exactDeviation = PositiveDeviation(side, plenionTime, exactTime);
        var sessionDeviation = PositiveDeviation(side, plenionTime, boundaryTime);
        var materiallyImproves = exactDeviation - sessionDeviation >= 15;
        var tripIds = component.Zip(component.Skip(1), (left, _) => left.OutgoingTripId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sessionId = $"worksite:{technician}:{block.Start:yyyyMMdd}:{string.Join('-', block.PerformanceIds)}";
        var reason = $"GPS-aanwezigheid gereconstrueerd uit {component.Length} stops en " +
                     $"{tripIds.Length} lokale ritten binnen dezelfde actieve Plenion-site/job; " +
                     $"{(side == DailyBoundarySide.First ? "duidelijke inbound rit" : "duidelijke outbound rit")}.";

        if (closeToRegisteredBoundary && hasRouteStructure && materiallyImproves)
        {
            var session = new WorksiteSession(
                sessionId,
                technician,
                DateOnly.FromDateTime(block.Start.DateTime),
                block.PerformanceIds,
                component[0].Arrival,
                component[^1].Departure,
                component.Select(item => item.StopId).ToArray(),
                tripIds,
                WorksiteSessionConfidence.Strong,
                reason);
            var selected = exact.Selected with
            {
                Stop = new MergedPilotStop(
                    sessionId,
                    session.Date,
                    session.ArrivalTime,
                    session.DepartureTime,
                    component.Sum(item => Math.Max(0, item.DurationMinutes)),
                    exact.Selected.Stop.Address,
                    exact.Selected.Stop.Latitude,
                    exact.Selected.Stop.Longitude,
                    exact.Selected.Stop.DriverId,
                    exact.Selected.Stop.DriverName,
                    session.StopIds,
                    false),
                OverlapMinutes = OverlapMinutes(block, session.ArrivalTime, session.DepartureTime),
                Explanation = exact.Selected.Explanation + "; WorksiteSession: " + reason,
            };
            return new WorksiteSessionDetection(
                exact with
                {
                    Selected = selected,
                    Candidates = exact.Candidates.Append(selected).ToArray(),
                    Assessment = "WorksiteSession: " + reason,
                    WorksiteSession = session,
                },
                true, true, false, 1, 0, stopwatch.Elapsed);
        }

        if (materiallyImproves && exactDeviation > 60)
        {
            var ambiguous = new WorksiteSession(
                sessionId,
                technician,
                DateOnly.FromDateTime(block.Start.DateTime),
                block.PerformanceIds,
                component[0].Arrival,
                component[^1].Departure,
                component.Select(item => item.StopId).ToArray(),
                tripIds,
                WorksiteSessionConfidence.Ambiguous,
                "Mogelijke werfcontinuïteit, maar route- of tijdsbewijs is onvoldoende voor automatische selectie.");
            return new WorksiteSessionDetection(
                exact with
                {
                    Decision = AdaptiveMatchDecision.Ambiguous,
                    Selected = null,
                    Assessment = "AmbiguousWorksiteSession: " + ambiguous.Reason,
                    WorksiteSession = ambiguous,
                },
                true, false, true, 1, 0, stopwatch.Elapsed);
        }

        return Result(exact, true, false, false, 1, stopwatch);
    }

    private static bool CanBridge(
        PilotStop left,
        PilotStop right,
        BoundaryBlock block,
        IReadOnlyList<NormalizedPilotPerformance> locationJobs,
        Dictionary<string, NormalizedPilotTrip> tripById)
    {
        if (!string.Equals(left.DriverId, right.DriverId, StringComparison.OrdinalIgnoreCase) ||
            !PowerfleetVehicleStreamIdentity.SamePhysicalStream(left, right) ||
            !string.Equals(left.OutgoingTripId, right.IncomingTripId, StringComparison.Ordinal) ||
            !tripById.TryGetValue(left.OutgoingTripId, out var trip) ||
            trip.DistanceKilometres > MaximumLocalTripKilometres ||
            trip.DrivingMinutes > MaximumLocalTripMinutes)
        {
            return false;
        }

        if (trip.StartDateTime < block.Start - BlockMargin ||
            trip.EndDateTime > block.End + BlockMargin)
        {
            return false;
        }

        var crossesAnotherJob = locationJobs.Any(job =>
            !block.PerformanceIds.Contains(job.ExternalId) &&
            job.StartDateTime <= trip.EndDateTime &&
            job.EndDateTime > trip.StartDateTime);
        if (crossesAnotherJob)
        {
            return false;
        }

        return left.DurationMinutes >= 3 || right.DurationMinutes >= 3 ||
               Math.Abs((left.Arrival - block.Start).TotalMinutes) <= BoundaryConfirmationMinutes ||
               Math.Abs((right.Departure - block.End).TotalMinutes) <= BoundaryConfirmationMinutes;
    }

    private static bool IsClearInbound(
        PilotStop stop,
        Dictionary<string, NormalizedPilotTrip> trips) =>
        trips.TryGetValue(stop.IncomingTripId, out var trip) && IsClearRoute(trip);

    private static bool IsClearOutbound(
        PilotStop stop,
        Dictionary<string, NormalizedPilotTrip> trips) =>
        trips.TryGetValue(stop.OutgoingTripId, out var trip) && IsClearRoute(trip);

    private static bool IsClearRoute(NormalizedPilotTrip trip) =>
        trip.DistanceKilometres > MaximumLocalTripKilometres ||
        trip.DrivingMinutes > MaximumLocalTripMinutes;

    private static int PositiveDeviation(
        DailyBoundarySide side,
        DateTimeOffset plenion,
        DateTimeOffset gps) =>
        HoursAuditService.PositiveWholeMinutes(side == DailyBoundarySide.First
            ? gps - plenion
            : plenion - gps);

    private static int OverlapMinutes(
        BoundaryBlock block,
        DateTimeOffset arrival,
        DateTimeOffset departure)
    {
        var start = arrival > block.Start ? arrival : block.Start;
        var end = departure < block.End ? departure : block.End;
        return end <= start ? 0 : (int)Math.Floor((end - start).TotalMinutes);
    }

    private static WorksiteSessionDetection Result(
        DailyBoundarySelection selection,
        bool considered,
        bool changed,
        bool ambiguous,
        int clusters,
        Stopwatch stopwatch) =>
        new(selection, considered, changed, ambiguous, clusters, 0, stopwatch.Elapsed);
}

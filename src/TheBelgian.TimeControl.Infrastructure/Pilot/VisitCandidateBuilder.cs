using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Builds VisitCandidates by aggregating consecutive Powerfleet stop fragments
/// that form one physical visit (same location, short gap, same day).
/// </summary>
internal static class VisitCandidateBuilder
{
    public static IReadOnlyList<VisitCandidate> Build(
        IReadOnlyList<PilotStop> stops,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator,
        bool requireLocationContinuity = true)
    {
        var ordered = stops
            .Where(stop =>
                stop.Latitude is not null &&
                stop.Longitude is not null &&
                (!requireLocationContinuity || stop.LocationContinuity))
            .OrderBy(stop => stop.Arrival)
            .ThenBy(stop => stop.StopId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var groups = new List<List<PilotStop>> { new List<PilotStop> { ordered[0] } };
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var group = groups[^1];
            var last = group[^1];
            if (CanExtendVisit(group, last, current, options, distanceCalculator))
            {
                group.Add(current);
            }
            else
            {
                groups.Add([current]);
            }
        }

        return groups.Select(group => ToVisit(group, distanceCalculator)).ToArray();
    }

    public static IReadOnlyList<MergedPilotStop> BuildMerged(
        IReadOnlyList<PilotStop> stops,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator) =>
        Build(stops, options, distanceCalculator)
            .Select(visit => visit.ToMergedPilotStop(options))
            .ToArray();

    private static bool CanExtendVisit(
        IReadOnlyList<PilotStop> group,
        PilotStop last,
        PilotStop current,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        if (current.Date != last.Date)
        {
            return false;
        }

        var sameDriver = string.Equals(
            last.DriverId,
            current.DriverId,
            StringComparison.OrdinalIgnoreCase);
        if (!sameDriver)
        {
            return false;
        }

        if (!PowerfleetVehicleStreamIdentity.SamePhysicalStream(last, current))
        {
            return false;
        }

        var gap = current.Arrival - last.Departure;
        if (gap > TimeSpan.FromMinutes(options.VisitMergeMaxGapMinutes))
        {
            return false;
        }

        // Arrival before previous departure is OK (overlapping fragments).
        if (gap < TimeSpan.FromMinutes(-options.VisitMergeMaxGapMinutes))
        {
            return false;
        }

        var toLast = DistanceMeters(last, current, distanceCalculator);
        if (toLast is null || toLast > options.VisitMergeDistanceMeters)
        {
            return false;
        }

        var centerLat = group.Average(item => (double)item.Latitude!.Value);
        var centerLon = group.Average(item => (double)item.Longitude!.Value);
        var toCenter = distanceCalculator.DistanceMetres(
            new GeoCoordinate(centerLat, centerLon),
            new GeoCoordinate((double)current.Latitude!.Value, (double)current.Longitude!.Value));
        if (toCenter > options.VisitMergeDistanceMeters)
        {
            return false;
        }

        return true;
    }

    private static double? DistanceMeters(
        PilotStop left,
        PilotStop right,
        IDistanceCalculator distanceCalculator)
    {
        if (left.Latitude is null ||
            left.Longitude is null ||
            right.Latitude is null ||
            right.Longitude is null)
        {
            return null;
        }

        return distanceCalculator.DistanceMetres(
            new GeoCoordinate((double)left.Latitude.Value, (double)left.Longitude.Value),
            new GeoCoordinate((double)right.Latitude.Value, (double)right.Longitude.Value));
    }

    private static VisitCandidate ToVisit(
        List<PilotStop> group,
        IDistanceCalculator distanceCalculator)
    {
        var arrival = group.Min(item => item.Arrival);
        var departure = group.Max(item => item.Departure);
        var dwell = Math.Max(
            0,
            (int)Math.Round((departure - arrival).TotalMinutes, MidpointRounding.AwayFromZero));
        var latitude = group.Average(item => (double)item.Latitude!.Value);
        var longitude = group.Average(item => (double)item.Longitude!.Value);
        var radius = 0d;
        if (group.Count > 1)
        {
            radius = group.Max(item =>
                distanceCalculator.DistanceMetres(
                    new GeoCoordinate(latitude, longitude),
                    new GeoCoordinate((double)item.Latitude!.Value, (double)item.Longitude!.Value)));
        }

        var ids = group.Select(item => item.StopId).Distinct(StringComparer.Ordinal).ToArray();
        var visitId = ids.Length == 1
            ? ids[0]
            : "visit:" + string.Join('+', ids);
        return new VisitCandidate(
            visitId,
            group[0].Date,
            arrival,
            departure,
            dwell,
            latitude,
            longitude,
            Math.Round(radius, 1),
            ids,
            group.Select(item => item.Address)
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToArray(),
            group);
    }
}

using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class MergedStopBuilder
{
    public static IReadOnlyList<MergedPilotStop> Merge(
        IReadOnlyList<PilotStop> stops,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var ordered = stops
            .Where(stop =>
                stop.Latitude is not null &&
                stop.Longitude is not null &&
                stop.LocationContinuity)
            .OrderBy(stop => stop.Arrival)
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
            var distance = distanceCalculator.DistanceMetres(
                new GeoCoordinate((double)last.Latitude!.Value, (double)last.Longitude!.Value),
                new GeoCoordinate(
                    (double)current.Latitude!.Value,
                    (double)current.Longitude!.Value));
            var sameDriver = string.Equals(
                last.DriverId,
                current.DriverId,
                StringComparison.OrdinalIgnoreCase);
            var contiguous = current.Arrival <= last.Departure.AddMinutes(15);
            if (sameDriver &&
                contiguous &&
                distance <= options.StopMergeDistanceMeters &&
                current.Date == last.Date)
            {
                group.Add(current);
            }
            else
            {
                groups.Add(new List<PilotStop> { current });
            }
        }

        return groups.Select(group =>
            {
                var arrival = group.Min(item => item.Arrival);
                var departure = group.Max(item => item.Departure);
                var duration = Math.Max(
                    0,
                    (int)Math.Round(
                        (departure - arrival).TotalMinutes,
                        MidpointRounding.AwayFromZero));
                var latitude = group.Average(item => (double)item.Latitude!.Value);
                var longitude = group.Average(item => (double)item.Longitude!.Value);
                return new MergedPilotStop(
                    "merged-" + group[0].StopId,
                    group[0].Date,
                    arrival,
                    departure,
                    duration,
                    group.Select(item => item.Address)
                        .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)),
                    (decimal)latitude,
                    (decimal)longitude,
                    group[0].DriverId,
                    group[0].DriverName,
                    group.Select(item => item.StopId).ToArray(),
                    duration < options.PassThroughMaxDurationMinutes);
            })
            .ToArray();
    }
}

using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class HistoricalLocationClusterLearner
{
    public static IReadOnlyList<HistoricalLocationCluster> Learn(
        IReadOnlyList<(
            NormalizedPilotPerformance Performance,
            string TechnicianName,
            PilotLocationResolution? Resolution,
            IReadOnlyList<MergedPilotStop> DayStops)> observations,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var byLocation = observations
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Performance.DeliveryAddressExternalId) ||
                !string.IsNullOrWhiteSpace(item.Resolution?.AddressHash))
            .SelectMany(item =>
            {
                var key = LocationKey(item.Performance, item.Resolution);
                var coordinate = item.Resolution?.Geocoding.Primary?.Coordinate;
                return item.DayStops
                    .Where(stop => !stop.IsPassThrough)
                    .Select(stop => new
                    {
                        Key = key,
                        item.Performance,
                        item.TechnicianName,
                        Stop = stop,
                        PlenionCoordinate = coordinate,
                    });
            })
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .SelectMany(group =>
                BuildClustersForLocation(
                    group.Key,
                    group.Select(item => (
                        item.Performance,
                        item.TechnicianName,
                        item.Stop,
                        item.PlenionCoordinate)).ToArray(),
                    options,
                    distanceCalculator))
            .ToArray();
        return byLocation;
    }

    private static HistoricalLocationCluster[] BuildClustersForLocation(
        string locationKey,
        (
            NormalizedPilotPerformance Performance,
            string TechnicianName,
            MergedPilotStop Stop,
            GeoCoordinate? PlenionCoordinate)[] visits,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var clusters = new List<List<(
            NormalizedPilotPerformance Performance,
            string TechnicianName,
            MergedPilotStop Stop,
            GeoCoordinate? PlenionCoordinate)>>();
        foreach (var visit in visits.OrderBy(item => item.Stop.Arrival))
        {
            var assigned = false;
            foreach (var cluster in clusters)
            {
                var centerLat = cluster.Average(item => (double)item.Stop.Latitude!.Value);
                var centerLon = cluster.Average(item => (double)item.Stop.Longitude!.Value);
                var distance = distanceCalculator.DistanceMetres(
                    new GeoCoordinate(centerLat, centerLon),
                    new GeoCoordinate(
                        (double)visit.Stop.Latitude!.Value,
                        (double)visit.Stop.Longitude!.Value));
                if (distance <= options.StopMergeDistanceMeters * 2)
                {
                    cluster.Add(visit);
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                clusters.Add([visit]);
            }
        }

        var totalVisits = visits.Length;
        var results = new List<HistoricalLocationCluster>();
        var index = 0;
        foreach (var cluster in clusters.OrderByDescending(item => item.Count))
        {
            index++;
            var centerLat = cluster.Average(item => (double)item.Stop.Latitude!.Value);
            var centerLon = cluster.Average(item => (double)item.Stop.Longitude!.Value);
            var distances = cluster
                .Where(item => item.PlenionCoordinate is not null)
                .Select(item => distanceCalculator.DistanceMetres(
                    item.PlenionCoordinate!.Value,
                    new GeoCoordinate(
                        (double)item.Stop.Latitude!.Value,
                        (double)item.Stop.Longitude!.Value)))
                .OrderBy(value => value)
                .ToArray();
            var overlaps = cluster.Select(item =>
                    OverlapMinutes(
                        item.Performance.StartDateTime,
                        item.Performance.EndDateTime,
                        item.Stop.Arrival,
                        item.Stop.Departure))
                .ToArray();
            var workdays = cluster.Select(item => item.Performance.Date).Distinct().Count();
            var technicians = cluster.Select(item => item.TechnicianName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var radius = cluster.Max(item =>
                distanceCalculator.DistanceMetres(
                    new GeoCoordinate(centerLat, centerLon),
                    new GeoCoordinate(
                        (double)item.Stop.Latitude!.Value,
                        (double)item.Stop.Longitude!.Value)));
            var averageDistance = distances.Length == 0 ? double.MaxValue : distances.Average();
            var medianDistance = distances.Length == 0
                ? double.MaxValue
                : distances[distances.Length / 2];
            var dominance = totalVisits == 0 ? 0 : 100d * cluster.Count / totalVisits;
            var competing = clusters.Count - 1;
            var confidence =
                Math.Min(
                    100,
                    (workdays * 12d) +
                    (dominance * 0.5) +
                    (overlaps.Average() > 0 ? 15 : 0) -
                    (competing * 8));
            results.Add(new HistoricalLocationCluster(
                $"{locationKey}#c{index}",
                locationKey,
                cluster[0].Performance.DeliveryAddressExternalId,
                centerLat,
                centerLon,
                Math.Round(radius, 1),
                cluster.Count,
                workdays,
                technicians,
                distances.Length == 0 ? 0 : Math.Round(averageDistance, 1),
                distances.Length == 0 ? 0 : Math.Round(medianDistance, 1),
                Math.Round(overlaps.Average(), 1),
                Math.Round(dominance, 1),
                competing,
                cluster.Min(item => item.Performance.Date),
                cluster.Max(item => item.Performance.Date),
                Math.Round(Math.Max(0, confidence), 1),
                options.CalculationVersion));
        }

            return results
            .Where(cluster =>
                cluster.DistinctWorkdayCount >= options.MinimumDistinctWorkdays &&
                cluster.DominancePercentage >= options.MinimumDominancePercentage &&
                cluster.MedianDistanceMeters <= options.MaximumDistanceFromPlenionMeters &&
                cluster.AverageOverlapMinutes >= options.MinimumOverlapMinutes)
            .ToArray();
    }

    public static string LocationKey(
        NormalizedPilotPerformance performance,
        PilotLocationResolution? resolution)
    {
        if (!string.IsNullOrWhiteSpace(performance.DeliveryAddressExternalId))
        {
            return "LACLEUNIK:" + performance.DeliveryAddressExternalId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(resolution?.AddressHash))
        {
            return "ADDR:" + resolution.AddressHash;
        }

        return "PERF:" + performance.ExternalId;
    }

    private static int OverlapMinutes(
        DateTimeOffset firstStart,
        DateTimeOffset firstEnd,
        DateTimeOffset secondStart,
        DateTimeOffset secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end <= start
            ? 0
            : (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero);
    }
}

using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class OfflineVisitMerge
{
    public sealed record Visit(
        IReadOnlyList<string> StopIds,
        DateTimeOffset Arrival,
        DateTimeOffset Departure,
        double? DistanceMeters);

    public static List<Visit> Merge(
        IReadOnlyList<LocationMatchingBenchmarkCandidate> candidates,
        AdaptiveLocationMatchingOptions options)
    {
        var ordered = candidates
            .OrderBy(item => item.Arrival)
            .ThenBy(item => item.StopId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var groups = new List<List<LocationMatchingBenchmarkCandidate>>();
        groups.Add([ordered[0]]);
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var group = groups[^1];
            var last = group[^1];
            var gap = current.Arrival - last.Departure;
            var nearInTime = gap <= TimeSpan.FromMinutes(options.VisitMergeMaxGapMinutes) &&
                             gap >= TimeSpan.FromMinutes(-options.VisitMergeMaxGapMinutes);
            var lastDist = last.DistanceMeters;
            var currentDist = current.DistanceMeters;
            var nearInSpace = lastDist is not null &&
                              currentDist is not null &&
                              Math.Abs(lastDist.Value - currentDist.Value) <= options.VisitMergeDistanceMeters &&
                              Math.Max(lastDist.Value, currentDist.Value) <=
                              options.VisitMergeDistanceMeters + options.StrongDistanceMeters;
            if (nearInTime && nearInSpace)
            {
                group.Add(current);
            }
            else
            {
                groups.Add([current]);
            }
        }

        return groups.Select(group => new Visit(
                group.Select(item => item.StopId).Distinct(StringComparer.Ordinal).ToArray(),
                group.Min(item => item.Arrival),
                group.Max(item => item.Departure),
                group.Where(item => item.DistanceMeters is not null)
                    .Select(item => item.DistanceMeters!.Value)
                    .DefaultIfEmpty(double.MaxValue)
                    .Min() is var distance && distance < double.MaxValue
                    ? distance
                    : null))
            .ToList();
    }

    public static int OverlapMinutes(
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

    public static bool MeetsShortChain(
        LocationMatchingBenchmarkCase item,
        Visit visit,
        int currentOverlap,
        AdaptiveLocationMatchingOptions options)
    {
        if (string.IsNullOrWhiteSpace(item.Lacleunik) ||
            currentOverlap < options.RecoveryShortChainMinOverlapMinutes)
        {
            return false;
        }

        foreach (var neighborText in new[] { item.PreviousPerformance, item.NextPerformance })
        {
            if (!TryParseNeighborWindow(item.Date, neighborText, out var neighborStart, out var neighborEnd))
            {
                continue;
            }

            // Require a directly adjacent neighbor window (small gap / contiguous).
            DateTimeOffset earlierEnd;
            DateTimeOffset laterStart;
            if (item.Start <= neighborStart)
            {
                earlierEnd = item.End;
                laterStart = neighborStart;
            }
            else
            {
                earlierEnd = neighborEnd;
                laterStart = item.Start;
            }

            var gapMinutes = (laterStart - earlierEnd).TotalMinutes;
            if (gapMinutes < -options.VisitMergeMaxGapMinutes ||
                gapMinutes > options.VisitMergeMaxGapMinutes)
            {
                continue;
            }

            var neighborOverlap = OverlapMinutes(
                neighborStart,
                neighborEnd,
                visit.Arrival,
                visit.Departure);
            if (neighborOverlap < options.RecoveryShortChainMinOverlapMinutes)
            {
                continue;
            }

            var visitMinutes = Math.Max(1, (visit.Departure - visit.Arrival).TotalMinutes);
            var performanceMinutes = Math.Max(1, (item.End - item.Start).TotalMinutes);
            var currentPercent = 100d * currentOverlap / performanceMinutes;
            // Boundary-only minutes on a long performance are not enough.
            if (currentOverlap < options.RecoveryMinimumOverlapMinutes &&
                currentPercent < options.MinimumOverlapPercent)
            {
                continue;
            }

            var chainStart = item.Start < neighborStart ? item.Start : neighborStart;
            var chainEnd = item.End > neighborEnd ? item.End : neighborEnd;
            var chainOverlap = OverlapMinutes(chainStart, chainEnd, visit.Arrival, visit.Departure);
            // Visit must be mostly dedicated to the adjacent same-LACLEUNIK chain window.
            if (100d * chainOverlap / visitMinutes >= options.RecoveryShortChainMinCombinedOverlapPercent)
            {
                return true;
            }
        }

        return false;
    }

    public static bool MeetsShortChain(
        RecoveryAuditCase item,
        Visit visit,
        int currentOverlap,
        AdaptiveLocationMatchingOptions options)
    {
        var shape = new LocationMatchingBenchmarkCase
        {
            PerformanceId = item.PerformanceId,
            Technician = item.Technician,
            Date = item.Date,
            Start = item.Start,
            End = item.End,
            Lacleunik = item.Lacleunik,
            PlenionAddress = item.PlenionAddress,
            GeocodeQuality = GeocodeQualityClass.PartialAddress,
            ExistingMatchStatus = "NoReliableMatch",
            PreviousPerformance = item.PreviousPerformance,
            NextPerformance = item.NextPerformance,
            Candidates = item.Candidates,
        };
        return MeetsShortChain(shape, visit, currentOverlap, options);
    }

    private static bool TryParseNeighborWindow(
        DateOnly date,
        string? text,
        out DateTimeOffset start,
        out DateTimeOffset end)
    {
        start = default;
        end = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var times = parts[^1].Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (times.Length != 2 ||
            !TimeOnly.TryParse(times[0], CultureInfo.InvariantCulture, out var startTime) ||
            !TimeOnly.TryParse(times[1], CultureInfo.InvariantCulture, out var endTime))
        {
            return false;
        }

        start = new DateTimeOffset(date.ToDateTime(startTime), TimeSpan.FromHours(2));
        end = new DateTimeOffset(date.ToDateTime(endTime), TimeSpan.FromHours(2));
        return true;
    }
}

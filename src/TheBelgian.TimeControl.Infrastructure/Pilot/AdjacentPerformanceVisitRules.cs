using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Shared rules for adjacent Plenion performances that share one physical VisitCandidate.
/// </summary>
internal static class AdjacentPerformanceVisitRules
{
    public static bool SameWorkLocation(
        NormalizedPilotPerformance left,
        NormalizedPilotPerformance right) =>
        !string.IsNullOrWhiteSpace(left.DeliveryAddressExternalId) &&
        string.Equals(
            left.DeliveryAddressExternalId,
            right.DeliveryAddressExternalId,
            StringComparison.OrdinalIgnoreCase);

    public static bool AreDirectlyAdjacent(
        NormalizedPilotPerformance left,
        NormalizedPilotPerformance right,
        AdaptiveLocationMatchingOptions options)
    {
        if (left.ExternalId == right.ExternalId || left.Date != right.Date)
        {
            return false;
        }

        // Gap between the earlier end and the later start (0 = contiguous).
        DateTimeOffset earlierEnd;
        DateTimeOffset laterStart;
        if (left.StartDateTime <= right.StartDateTime)
        {
            earlierEnd = left.EndDateTime;
            laterStart = right.StartDateTime;
        }
        else
        {
            earlierEnd = right.EndDateTime;
            laterStart = left.StartDateTime;
        }

        var gapMinutes = (laterStart - earlierEnd).TotalMinutes;
        return gapMinutes >= -options.VisitMergeMaxGapMinutes &&
               gapMinutes <= options.VisitMergeMaxGapMinutes;
    }

    public static bool MeetsSharedVisitChain(
        NormalizedPilotPerformance performance,
        MergedPilotStop visit,
        double currentOverlapMinutes,
        double currentOverlapPercent,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        AdaptiveLocationMatchingOptions options,
        out NormalizedPilotPerformance? neighbor,
        out double chainCoveragePercent)
    {
        neighbor = null;
        chainCoveragePercent = 0;
        if (string.IsNullOrWhiteSpace(performance.DeliveryAddressExternalId))
        {
            return false;
        }

        if (currentOverlapMinutes < options.RecoveryShortChainMinOverlapMinutes)
        {
            return false;
        }

        // Boundary-only minutes on a long performance are not enough for the chain exception
        // (keeps 280346-style leakage out while allowing short slots like 279971 at ~30%).
        if (currentOverlapMinutes < options.RecoveryMinimumOverlapMinutes &&
            currentOverlapPercent < options.MinimumOverlapPercent)
        {
            return false;
        }

        var candidates = sameDayPerformances
            .Where(item => item.ExternalId != performance.ExternalId)
            .Where(item => SameWorkLocation(performance, item))
            .Where(item => AreDirectlyAdjacent(performance, item, options))
            .Select(item =>
            {
                var overlap = OverlapMinutes(
                    item.StartDateTime,
                    item.EndDateTime,
                    visit.Arrival,
                    visit.Departure);
                return (Performance: item, Overlap: overlap);
            })
            .Where(item => item.Overlap >= options.RecoveryShortChainMinOverlapMinutes)
            .OrderBy(item =>
                Math.Min(
                    Math.Abs((item.Performance.StartDateTime - performance.EndDateTime).TotalMinutes),
                    Math.Abs((item.Performance.EndDateTime - performance.StartDateTime).TotalMinutes)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        neighbor = candidates[0].Performance;
        var chainStart = performance.StartDateTime < neighbor.StartDateTime
            ? performance.StartDateTime
            : neighbor.StartDateTime;
        var chainEnd = performance.EndDateTime > neighbor.EndDateTime
            ? performance.EndDateTime
            : neighbor.EndDateTime;
        var chainOverlap = OverlapMinutes(chainStart, chainEnd, visit.Arrival, visit.Departure);
        var visitMinutes = Math.Max(
            1,
            (visit.Departure - visit.Arrival).TotalMinutes);
        // Visit must be dedicated to the chain (most of the visit falls inside the chain window).
        chainCoveragePercent = 100d * chainOverlap / visitMinutes;
        return chainCoveragePercent >= options.RecoveryShortChainMinCombinedOverlapPercent;
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
}

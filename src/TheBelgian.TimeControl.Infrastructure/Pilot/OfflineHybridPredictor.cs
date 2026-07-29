using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Shared offline hybrid prediction used by calibration/audit evaluation and frozen verification.
/// Does not change matching thresholds; mirrors PrecisionPreservingHybridMatcher recovery gates
/// on stored VisitCandidate-style candidates.
/// </summary>
internal static class OfflineHybridPredictor
{
    public sealed record Prediction(
        bool Accepted,
        string Decision,
        string? StopId,
        IReadOnlyList<string> SourceStopIds,
        bool UsedRecovery);

    public static Prediction Predict(
        LocationMatchingBenchmarkCase item,
        AdaptiveLocationMatchingOptions options,
        bool recovery)
    {
        var visits = OfflineVisitMerge.Merge(item.Candidates, options);
        if (visits.Count == 0)
        {
            return new Prediction(false, "Unresolved", null, [], false);
        }

        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
        var best = visits
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    item.Start,
                    item.End,
                    visit.Arrival,
                    visit.Departure);
                return (
                    Visit: visit,
                    Overlap: overlap,
                    OverlapPercent: 100d * overlap / performanceMinutes,
                    Distance: visit.DistanceMeters ?? double.MaxValue);
            })
            .OrderByDescending(entry => entry.Overlap)
            .ThenBy(entry => entry.Distance)
            .First();

        // Never accept a visit that starts at/after performance end, even if a prior
        // baseline status said Probable (post-end false-positive pattern).
        var temporallyValid =
            best.Visit.Arrival < item.End &&
            best.Visit.Departure > item.Start &&
            best.Overlap > 0;
        var spatiallyPlausible = best.Distance <= options.RecoveryMaximumDistanceMeters;
        var adaptiveAccepted =
            temporallyValid &&
            spatiallyPlausible &&
            item.ExistingMatchStatus is "ConfirmedLocationMatch" or "ProbableLocationMatch";
        if (!recovery)
        {
            return adaptiveAccepted
                ? new Prediction(true, "Probable", best.Visit.StopIds[0], best.Visit.StopIds, false)
                : new Prediction(false, "Unresolved", null, [], false);
        }

        if (adaptiveAccepted)
        {
            return new Prediction(true, "Probable", best.Visit.StopIds[0], best.Visit.StopIds, false);
        }

        var shortChain = OfflineVisitMerge.MeetsShortChain(
            item,
            best.Visit,
            best.Overlap,
            options);
        var overlapEnough =
            best.Overlap >= options.RecoveryMinimumOverlapMinutes ||
            best.OverlapPercent >= options.RecoveryMinimumOverlapPercent;
        var canRecover =
            temporallyValid &&
            spatiallyPlausible &&
            item.GeocodeQuality != GeocodeQualityClass.Unusable &&
            (overlapEnough || shortChain);
        return canRecover
            ? new Prediction(true, "RecoveredProbable", best.Visit.StopIds[0], best.Visit.StopIds, true)
            : new Prediction(false, "Unresolved", null, [], false);
    }

    public static RecoveryAuditCase RescoreAuditCase(
        RecoveryAuditCase item,
        AdaptiveLocationMatchingOptions options)
    {
        var visits = OfflineVisitMerge.Merge(item.Candidates, options);
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
        if (visits.Count == 0)
        {
            return item with
            {
                HybridDecision = "Unresolved",
                UsedRecovery = false,
                SelectedStopId = null,
                SelectedSourceStopIds = [],
            };
        }

        var best = visits
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    item.Start,
                    item.End,
                    visit.Arrival,
                    visit.Departure);
                return (
                    Visit: visit,
                    Overlap: overlap,
                    OverlapPercent: 100d * overlap / performanceMinutes,
                    Distance: visit.DistanceMeters ?? double.MaxValue);
            })
            .OrderByDescending(entry => entry.Overlap)
            .ThenBy(entry => entry.Distance)
            .First();

        var sources = best.Visit.StopIds;
        var distanceZone = best.Distance switch
        {
            <= 100 => "Strong0To100",
            <= 250 => "Probable101To250",
            <= 500 => "Learned251To500",
            _ when best.Distance < double.MaxValue => "Beyond500",
            _ => item.DistanceZone ?? "Unknown",
        };

        var adaptiveAccepted = item.AdaptiveDecision is "Confirmed" or "Probable"
            or "ConfirmedLocationMatch" or "ProbableLocationMatch";
        string hybridDecision;
        var usedRecovery = false;
        if (adaptiveAccepted)
        {
            hybridDecision = item.AdaptiveDecision is "Confirmed" or "ConfirmedLocationMatch"
                ? "Confirmed"
                : "Probable";
        }
        else if (CanRecover(item, best.Visit, best.Overlap, best.OverlapPercent, best.Distance, options))
        {
            usedRecovery = true;
            hybridDecision = "RecoveredProbable";
        }
        else
        {
            hybridDecision = item.AdaptiveDecision is "Ambiguous" ? "Ambiguous" : "Unresolved";
        }

        return item with
        {
            HybridDecision = hybridDecision,
            UsedRecovery = usedRecovery,
            SelectedStopId = sources[0],
            SelectedSourceStopIds = sources,
            SelectedDistanceMeters = best.Distance < double.MaxValue ? best.Distance : null,
            SelectedOverlapMinutes = best.Overlap,
            SelectedOverlapPercent = Math.Round(best.OverlapPercent, 1),
            DistanceZone = distanceZone,
        };
    }

    private static bool CanRecover(
        RecoveryAuditCase item,
        OfflineVisitMerge.Visit visit,
        int overlap,
        double overlapPercent,
        double distance,
        AdaptiveLocationMatchingOptions options)
    {
        if (visit.Arrival >= item.End || visit.Departure <= item.Start || overlap <= 0)
        {
            return false;
        }

        if (distance > options.RecoveryMaximumDistanceMeters)
        {
            return false;
        }

        if (string.Equals(item.GeocodeQuality, "Unusable", StringComparison.Ordinal))
        {
            return false;
        }

        var shortChain = OfflineVisitMerge.MeetsShortChain(item, visit, overlap, options);
        var overlapEnough =
            overlap >= options.RecoveryMinimumOverlapMinutes ||
            overlapPercent >= options.RecoveryMinimumOverlapPercent;
        return overlapEnough || shortChain;
    }
}

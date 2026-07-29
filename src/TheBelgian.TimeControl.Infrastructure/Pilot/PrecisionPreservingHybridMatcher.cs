using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Keeps adaptive as the precision-safe base and recovers only strong Unresolved
/// candidates that have positive overlap and clear location evidence.
/// Explicitly rejects stops that start after the performance ends (e.g. 280198).
/// </summary>
internal static class PrecisionPreservingHybridMatcher
{
    public static AdaptiveMatchResult Match(
        NormalizedPilotPerformance performance,
        string technicianName,
        PilotLocationResolution baselineResolution,
        IReadOnlyList<MergedPilotStop> dayStops,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clustersByLocation,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var adaptive = AdaptiveLocationMatcher.Match(
            performance,
            technicianName,
            baselineResolution,
            dayStops,
            sameDayPerformances,
            clustersByLocation,
            options,
            distanceCalculator,
            enableLearning: false);

        if (adaptive.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable ||
            !options.EnablePrecisionPreservingRecovery)
        {
            return adaptive;
        }

        if (adaptive.Decision != AdaptiveMatchDecision.Unresolved ||
            adaptive.Candidates.Count == 0)
        {
            return adaptive;
        }

        if (!TryRecover(
                performance,
                sameDayPerformances,
                adaptive.Candidates,
                adaptive.GeocodeQuality,
                options,
                out var selected,
                out var reason))
        {
            return adaptive;
        }

        return adaptive with
        {
            Decision = AdaptiveMatchDecision.Probable,
            Selected = selected,
            UsedHistoricalCluster = selected.HistoricalClusterId is not null,
            DistanceZone = selected.DistanceZone,
            Assessment = reason,
            UsedRecovery = true,
            RecoveryReason = reason,
        };
    }

    public static bool TryRecover(
        NormalizedPilotPerformance performance,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        IReadOnlyList<AdaptiveMatchCandidate> candidates,
        GeocodeQualityClass geocodeQuality,
        AdaptiveLocationMatchingOptions options,
        out AdaptiveMatchCandidate selected,
        out string reason)
    {
        selected = candidates[0];
        reason = string.Empty;

        // Stops that begin only after the performance ends must never be recovered.
        if (selected.Stop.Arrival >= performance.EndDateTime)
        {
            return false;
        }

        if (selected.Stop.Departure <= performance.StartDateTime)
        {
            return false;
        }

        if (selected.OverlapMinutes <= 0)
        {
            return false;
        }

        var overlapEnough =
            selected.OverlapMinutes >= options.RecoveryMinimumOverlapMinutes ||
            selected.OverlapPercent >= options.RecoveryMinimumOverlapPercent;
        if (!overlapEnough)
        {
            return false;
        }

        if (!HasLocationEvidence(selected, geocodeQuality, options))
        {
            return false;
        }

        if (candidates.Count > 1)
        {
            var margin = selected.TotalScore - candidates[1].TotalScore;
            if (margin < options.RecoveryMinimumScoreMargin)
            {
                return false;
            }
        }

        if (BelongsMoreToNeighbor(performance, selected, sameDayPerformances))
        {
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"Recovery: positive overlap {selected.OverlapMinutes} min ({selected.OverlapPercent:0.#}%), " +
            $"distance {selected.DistanceMeters?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a"} m " +
            $"({selected.DistanceZone}), geocode {geocodeQuality}, " +
            $"no comparable competitor, not owned by neighbor.");
        return true;
    }

    private static bool HasLocationEvidence(
        AdaptiveMatchCandidate candidate,
        GeocodeQualityClass geocodeQuality,
        AdaptiveLocationMatchingOptions options)
    {
        if (candidate.DistanceMeters is null ||
            candidate.DistanceMeters > options.RecoveryMaximumDistanceMeters)
        {
            return false;
        }

        if (geocodeQuality == GeocodeQualityClass.Unusable)
        {
            return false;
        }

        var strongOverlap =
            candidate.OverlapMinutes >= options.RecoveryStrongOverlapMinutes ||
            candidate.OverlapPercent >= options.RecoveryStrongOverlapPercent;

        // LowConfidence needs strong temporal support plus in-range distance.
        if (geocodeQuality == GeocodeQualityClass.LowConfidence)
        {
            return strongOverlap &&
                   candidate.DistanceZone is AdaptiveDistanceZone.Strong0To100
                       or AdaptiveDistanceZone.Probable101To250;
        }

        if (candidate.DistanceZone == AdaptiveDistanceZone.Strong0To100)
        {
            return true;
        }

        if (candidate.DistanceZone != AdaptiveDistanceZone.Probable101To250)
        {
            return false;
        }

        if (strongOverlap)
        {
            // StreetOnly/Partial allowed with strong temporal support.
            return true;
        }

        // Weaker overlap in the probable band needs PartialAddress or better (279971).
        return geocodeQuality is GeocodeQualityClass.PreciseBuilding
            or GeocodeQualityClass.PreciseAmenity
            or GeocodeQualityClass.PartialAddress;
    }

    private static bool BelongsMoreToNeighbor(
        NormalizedPilotPerformance performance,
        AdaptiveMatchCandidate candidate,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances)
    {
        // Spanning stops may continue into the next performance (280344). Reject only when
        // the stop clearly opens inside another performance without tight alignment to current.
        var arrivalAlignmentToCurrent = Math.Abs(
            (candidate.Stop.Arrival - performance.StartDateTime).TotalMinutes);

        foreach (var neighbor in sameDayPerformances)
        {
            if (neighbor.ExternalId == performance.ExternalId)
            {
                continue;
            }

            var arrivesInsideNeighbor =
                candidate.Stop.Arrival >= neighbor.StartDateTime &&
                candidate.Stop.Arrival < neighbor.EndDateTime;
            if (!arrivesInsideNeighbor)
            {
                continue;
            }

            if (arrivalAlignmentToCurrent <= 30)
            {
                var neighborOverlap = OverlapMinutes(
                    neighbor.StartDateTime,
                    neighbor.EndDateTime,
                    candidate.Stop.Arrival,
                    candidate.Stop.Departure);
                if (neighborOverlap <= candidate.OverlapMinutes)
                {
                    continue;
                }
            }

            return true;
        }

        return false;
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

using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Keeps adaptive as the precision-safe base and recovers only strong Unresolved/Ambiguous
/// candidates that have sufficient overlap (or a short same-LACLEUNIK chain exception).
/// Explicitly rejects visits that start after the performance ends.
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

        if (adaptive.Decision is not (AdaptiveMatchDecision.Unresolved
                or AdaptiveMatchDecision.Ambiguous) ||
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

        // Visits that begin only after the performance ends must never be recovered.
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

        var shortChain = MeetsShortChainException(
            performance,
            selected,
            sameDayPerformances,
            options);
        var overlapEnough =
            selected.OverlapMinutes >= options.RecoveryMinimumOverlapMinutes ||
            selected.OverlapPercent >= options.RecoveryMinimumOverlapPercent;
        if (!overlapEnough && !shortChain)
        {
            return false;
        }

        if (!HasLocationEvidence(selected, geocodeQuality, options, shortChain))
        {
            return false;
        }

        if (!shortChain &&
            candidates.Count > 1 &&
            selected.TotalScore - candidates[1].TotalScore < options.RecoveryMinimumScoreMargin)
        {
            return false;
        }

        if (BelongsMoreToNeighbor(performance, selected, sameDayPerformances, options))
        {
            return false;
        }

        reason = shortChain
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Recovery(short-chain): overlap {selected.OverlapMinutes} min ({selected.OverlapPercent:0.#}%), " +
                $"distance {selected.DistanceMeters?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a"} m " +
                $"({selected.DistanceZone}), same LACLEUNIK visit chain.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Recovery: positive overlap {selected.OverlapMinutes} min ({selected.OverlapPercent:0.#}%), " +
                $"distance {selected.DistanceMeters?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a"} m " +
                $"({selected.DistanceZone}), geocode {geocodeQuality}, " +
                $"no comparable competitor, not owned by neighbor.");
        return true;
    }

    private static bool MeetsShortChainException(
        NormalizedPilotPerformance performance,
        AdaptiveMatchCandidate selected,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        AdaptiveLocationMatchingOptions options)
    {
        var lac = performance.DeliveryAddressExternalId;
        if (string.IsNullOrWhiteSpace(lac))
        {
            return false;
        }

        var adjacent = sameDayPerformances
            .Where(item =>
                item.ExternalId != performance.ExternalId &&
                string.Equals(
                    item.DeliveryAddressExternalId,
                    lac,
                    StringComparison.OrdinalIgnoreCase))
            .Where(item =>
                OverlapMinutes(
                    item.StartDateTime,
                    item.EndDateTime,
                    selected.Stop.Arrival,
                    selected.Stop.Departure) >= options.RecoveryShortChainMinOverlapMinutes)
            .OrderBy(item => item.StartDateTime)
            .ToArray();
        if (adjacent.Length == 0)
        {
            return false;
        }

        if (selected.OverlapMinutes < options.RecoveryShortChainMinOverlapMinutes)
        {
            return false;
        }

        // Pick the temporally closest adjacent performance with same LACLEUNIK.
        var neighbor = adjacent
            .OrderBy(item =>
                Math.Min(
                    Math.Abs((item.StartDateTime - performance.EndDateTime).TotalMinutes),
                    Math.Abs((item.EndDateTime - performance.StartDateTime).TotalMinutes)))
            .First();

        var chainStart = performance.StartDateTime < neighbor.StartDateTime
            ? performance.StartDateTime
            : neighbor.StartDateTime;
        var chainEnd = performance.EndDateTime > neighbor.EndDateTime
            ? performance.EndDateTime
            : neighbor.EndDateTime;
        var chainMinutes = Math.Max(1, (chainEnd - chainStart).TotalMinutes);
        var chainOverlap = OverlapMinutes(
            chainStart,
            chainEnd,
            selected.Stop.Arrival,
            selected.Stop.Departure);
        var chainPercent = 100d * chainOverlap / chainMinutes;
        return chainPercent >= options.RecoveryShortChainMinCombinedOverlapPercent;
    }

    private static bool HasLocationEvidence(
        AdaptiveMatchCandidate candidate,
        GeocodeQualityClass geocodeQuality,
        AdaptiveLocationMatchingOptions options,
        bool shortChain)
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
            candidate.OverlapPercent >= options.RecoveryStrongOverlapPercent ||
            shortChain;

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
            return true;
        }

        return geocodeQuality is GeocodeQualityClass.PreciseBuilding
            or GeocodeQualityClass.PreciseAmenity
            or GeocodeQualityClass.PartialAddress;
    }

    private static bool BelongsMoreToNeighbor(
        NormalizedPilotPerformance performance,
        AdaptiveMatchCandidate candidate,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        AdaptiveLocationMatchingOptions options)
    {
        var lac = performance.DeliveryAddressExternalId;
        var arrivalAlignmentToCurrent = Math.Abs(
            (candidate.Stop.Arrival - performance.StartDateTime).TotalMinutes);

        foreach (var neighbor in sameDayPerformances)
        {
            if (neighbor.ExternalId == performance.ExternalId)
            {
                continue;
            }

            var sameLac = !string.IsNullOrWhiteSpace(lac) &&
                          string.Equals(
                              neighbor.DeliveryAddressExternalId,
                              lac,
                              StringComparison.OrdinalIgnoreCase);
            var neighborOverlap = OverlapMinutes(
                neighbor.StartDateTime,
                neighbor.EndDateTime,
                candidate.Stop.Arrival,
                candidate.Stop.Departure);

            // Same LACLEUNIK chain: shared visit is allowed (handled by short-chain recovery).
            if (sameLac)
            {
                continue;
            }

            // Different location: reject small boundary leakage onto the neighbor's visit.
            if (candidate.Stop.Arrival >= performance.EndDateTime)
            {
                return true;
            }

            var arrivesInsideNeighbor =
                candidate.Stop.Arrival >= neighbor.StartDateTime &&
                candidate.Stop.Arrival < neighbor.EndDateTime;
            if (arrivesInsideNeighbor &&
                neighborOverlap > candidate.OverlapMinutes)
            {
                return true;
            }

            if (arrivesInsideNeighbor &&
                arrivalAlignmentToCurrent > options.MaximumArrivalDifferenceMinutes &&
                neighborOverlap >= options.RecoveryShortChainMinOverlapMinutes)
            {
                return true;
            }

            // Tiny current overlap while neighbor owns the visit.
            if (candidate.OverlapMinutes < options.RecoveryMinimumOverlapMinutes &&
                neighborOverlap > candidate.OverlapMinutes &&
                neighborOverlap >= options.RecoveryShortChainMinOverlapMinutes)
            {
                return true;
            }
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

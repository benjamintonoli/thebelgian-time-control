using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class AdaptiveLocationMatcher
{
    public static AdaptiveMatchResult Match(
        NormalizedPilotPerformance performance,
        string technicianName,
        PilotLocationResolution baselineResolution,
        IReadOnlyList<MergedPilotStop> dayStops,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clustersByLocation,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator,
        bool enableLearning)
    {
        var geocodeQuality = GeocodeQualityClassifier.Classify(baselineResolution.Geocoding);
        var precisePoint = GeocodeQualityClassifier.CanUseAsPrecisePoint(geocodeQuality);
        var plenionCoordinate = baselineResolution.Geocoding.Primary?.Coordinate;
        var locationKey = HistoricalLocationClusterLearner.LocationKey(
            performance,
            baselineResolution);
        clustersByLocation.TryGetValue(locationKey, out var cluster);
        var candidates = dayStops
            .Where(stop => !stop.IsPassThrough ||
                           OverlapMinutes(
                               performance.StartDateTime,
                               performance.EndDateTime,
                               stop.Arrival,
                               stop.Departure) > 0)
            .Select(stop => ScoreCandidate(
                performance,
                stop,
                sameDayPerformances,
                plenionCoordinate,
                precisePoint,
                geocodeQuality,
                cluster,
                enableLearning,
                options,
                distanceCalculator))
            .OrderByDescending(item => item.TotalScore)
            .ThenBy(item => item.DistanceMeters ?? double.MaxValue)
            .ToArray();

        var decision = Decide(candidates, options, precisePoint, enableLearning && cluster is not null);
        AdaptiveMatchCandidate? selected = decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable
            ? candidates[0]
            : null;
        return new AdaptiveMatchResult(
            performance.ExternalId,
            performance.Date,
            technicianName,
            performance.DeliveryAddressExternalId,
            baselineResolution.OriginalAddress,
            geocodeQuality,
            precisePoint,
            decision,
            selected,
            candidates.Take(5).ToArray(),
            selected?.HistoricalClusterId is not null,
            selected?.DistanceZone ?? AdaptiveDistanceZone.Unknown,
            Assessment(decision, geocodeQuality, selected, candidates));
    }

    private static AdaptiveMatchCandidate ScoreCandidate(
        NormalizedPilotPerformance performance,
        MergedPilotStop stop,
        IReadOnlyList<NormalizedPilotPerformance> sameDayPerformances,
        GeoCoordinate? plenionCoordinate,
        bool precisePoint,
        GeocodeQualityClass geocodeQuality,
        HistoricalLocationCluster? cluster,
        bool enableLearning,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        double? distance = null;
        if (plenionCoordinate is not null &&
            stop.Latitude is not null &&
            stop.Longitude is not null)
        {
            distance = distanceCalculator.DistanceMetres(
                plenionCoordinate.Value,
                new GeoCoordinate((double)stop.Latitude.Value, (double)stop.Longitude.Value));
        }

        var zone = ClassifyDistance(distance, options);
        var overlap = OverlapMinutes(
            performance.StartDateTime,
            performance.EndDateTime,
            stop.Arrival,
            stop.Departure);
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round(
                (performance.EndDateTime - performance.StartDateTime).TotalMinutes -
                performance.PauseMinutes,
                MidpointRounding.AwayFromZero));
        var overlapPercent = 100d * overlap / performanceMinutes;
        var arrivalDiff = RoundedMinutes(stop.Arrival - performance.StartDateTime);
        var departureDiff = RoundedMinutes(stop.Departure - performance.EndDateTime);
        var competing = sameDayPerformances.Any(other =>
            other.ExternalId != performance.ExternalId &&
            !AdjacentPerformanceVisitRules.SameWorkLocation(performance, other) &&
            OverlapMinutes(
                other.StartDateTime,
                other.EndDateTime,
                stop.Arrival,
                stop.Departure) > 0);
        var geocodeScore = GeocodeQualityClassifier.Score(geocodeQuality);
        var distanceScore = DistanceScore(distance, zone, precisePoint, options);
        var timeScore = overlap >= options.MinimumOverlapMinutes
            ? Math.Min(20, 8 + overlap / 5.0)
            : overlap > 0 ? 4 : 0;
        var overlapPercentScore = overlapPercent >= options.StrongOverlapPercent
            ? 15
            : overlapPercent >= options.MinimumOverlapPercent
                ? 10
                : overlapPercent > 0
                    ? 4
                    : 0;
        var alignmentScore = 0d;
        if (Math.Abs(arrivalDiff) <= options.MaximumArrivalDifferenceMinutes)
        {
            alignmentScore += 5;
        }

        if (Math.Abs(departureDiff) <= options.MaximumDepartureDifferenceMinutes)
        {
            alignmentScore += 5;
        }

        var historicalScore = 0d;
        string? clusterId = null;
        if (enableLearning &&
            cluster is not null &&
            stop.Latitude is not null &&
            stop.Longitude is not null)
        {
            var toCluster = distanceCalculator.DistanceMetres(
                new GeoCoordinate(cluster.CenterLatitude, cluster.CenterLongitude),
                new GeoCoordinate((double)stop.Latitude.Value, (double)stop.Longitude.Value));
            if (toCluster <= Math.Max(cluster.RadiusMeters, options.StopMergeDistanceMeters) &&
                (distance is null ||
                 distance <= options.MaximumLearnedClusterDistanceMeters))
            {
                historicalScore = Math.Min(20, cluster.Confidence / 5.0);
                clusterId = cluster.ClusterId;
            }
        }

        var competitionPenalty = competing ? 12 : 0;
        if (!precisePoint && zone is AdaptiveDistanceZone.Strong0To100
            or AdaptiveDistanceZone.Probable101To250)
        {
            distanceScore *= 0.35;
        }

        if (zone == AdaptiveDistanceZone.Learned251To500 && clusterId is null)
        {
            distanceScore = 0;
            historicalScore = 0;
        }

        if (zone == AdaptiveDistanceZone.Beyond500)
        {
            distanceScore = 0;
            historicalScore = 0;
        }

        var total = Math.Max(
            0,
            geocodeScore +
            distanceScore +
            timeScore +
            overlapPercentScore +
            alignmentScore +
            historicalScore -
            competitionPenalty);
        return new AdaptiveMatchCandidate(
            stop,
            distance is null ? null : Math.Round(distance.Value, 1),
            zone,
            overlap,
            Math.Round(overlapPercent, 1),
            arrivalDiff,
            departureDiff,
            stop.DurationMinutes,
            competing,
            geocodeScore,
            Math.Round(distanceScore, 1),
            Math.Round(timeScore, 1),
            overlapPercentScore,
            alignmentScore,
            Math.Round(historicalScore, 1),
            competitionPenalty,
            Math.Round(total, 1),
            clusterId,
            BuildExplanation(
                zone,
                overlap,
                overlapPercent,
                clusterId,
                competing,
                precisePoint));
    }

    private static AdaptiveMatchDecision Decide(
        AdaptiveMatchCandidate[] candidates,
        AdaptiveLocationMatchingOptions options,
        bool precisePoint,
        bool learningEnabled)
    {
        if (candidates.Length == 0)
        {
            return AdaptiveMatchDecision.Unresolved;
        }

        var best = candidates[0];
        if (candidates.Length > 1 &&
            best.TotalScore - candidates[1].TotalScore < options.MinimumScoreMargin)
        {
            return AdaptiveMatchDecision.Ambiguous;
        }

        if (best.DistanceZone == AdaptiveDistanceZone.Beyond500)
        {
            return AdaptiveMatchDecision.Unresolved;
        }

        if (best.DistanceZone == AdaptiveDistanceZone.Learned251To500 &&
            best.HistoricalClusterId is null)
        {
            return AdaptiveMatchDecision.Unresolved;
        }

        var strongTime =
            best.OverlapMinutes >= options.MinimumOverlapMinutes &&
            best.OverlapPercent >= options.MinimumOverlapPercent;
        if (best.DistanceZone == AdaptiveDistanceZone.Probable101To250 &&
            (!strongTime || best.HasCompetingPerformanceOverlap))
        {
            return AdaptiveMatchDecision.Unresolved;
        }

        if (best.DistanceZone == AdaptiveDistanceZone.Strong0To100 &&
            !strongTime &&
            best.OverlapMinutes <= 0)
        {
            return AdaptiveMatchDecision.Unresolved;
        }

        if (!precisePoint &&
            best.HistoricalClusterId is null &&
            best.DistanceZone is not AdaptiveDistanceZone.Strong0To100)
        {
            // Street/city geocode without learning: do not auto-confirm far candidates.
            if (best.TotalScore < options.ConfirmedMinimumScore)
            {
                return best.TotalScore >= options.ProbableMinimumScore && strongTime
                    ? AdaptiveMatchDecision.Probable
                    : AdaptiveMatchDecision.Unresolved;
            }
        }

        if (best.TotalScore >= options.ConfirmedMinimumScore &&
            (precisePoint || best.HistoricalClusterId is not null || strongTime))
        {
            return AdaptiveMatchDecision.Confirmed;
        }

        if (best.TotalScore >= options.ProbableMinimumScore && strongTime)
        {
            return AdaptiveMatchDecision.Probable;
        }

        _ = learningEnabled;
        return AdaptiveMatchDecision.Unresolved;
    }

    private static AdaptiveDistanceZone ClassifyDistance(
        double? distance,
        AdaptiveLocationMatchingOptions options)
    {
        if (distance is null)
        {
            return AdaptiveDistanceZone.Unknown;
        }

        if (distance <= options.StrongDistanceMeters)
        {
            return AdaptiveDistanceZone.Strong0To100;
        }

        if (distance <= options.ProbableDistanceMeters)
        {
            return AdaptiveDistanceZone.Probable101To250;
        }

        if (distance <= options.MaximumLearnedClusterDistanceMeters)
        {
            return AdaptiveDistanceZone.Learned251To500;
        }

        return AdaptiveDistanceZone.Beyond500;
    }

    private static double DistanceScore(
        double? distance,
        AdaptiveDistanceZone zone,
        bool precisePoint,
        AdaptiveLocationMatchingOptions options) =>
        zone switch
        {
            AdaptiveDistanceZone.Strong0To100 => precisePoint ? 30 : 18,
            AdaptiveDistanceZone.Probable101To250 => precisePoint ? 18 : 8,
            AdaptiveDistanceZone.Learned251To500 => 10,
            _ => 0,
        };

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
            : RoundedMinutes(end - start);
    }

    private static int RoundedMinutes(TimeSpan value) =>
        (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);

    private static string BuildExplanation(
        AdaptiveDistanceZone zone,
        int overlap,
        double overlapPercent,
        string? clusterId,
        bool competing,
        bool precisePoint) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{zone}; overlap {overlap} min ({overlapPercent:0.#}%); " +
            $"preciseGeocode={precisePoint}; cluster={clusterId ?? "none"}; competing={competing}");

    private static string Assessment(
        AdaptiveMatchDecision decision,
        GeocodeQualityClass quality,
        AdaptiveMatchCandidate? selected,
        AdaptiveMatchCandidate[] candidates) =>
        decision switch
        {
            AdaptiveMatchDecision.Confirmed =>
                $"Automatisch bevestigd ({quality}) via {selected?.Stop.Address}; score {selected?.TotalScore}.",
            AdaptiveMatchDecision.Probable =>
                $"Waarschijnlijk ({quality}); score {selected?.TotalScore}.",
            AdaptiveMatchDecision.Ambiguous =>
                candidates.Length > 1
                    ? $"Ambiguous: top scores {candidates[0].TotalScore} vs {candidates[1].TotalScore}."
                    : "Ambiguous zonder duidelijke voorsprong.",
            _ => $"Unresolved ({quality}).",
        };
}

using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal enum DailyBoundarySide { First, Last }

internal sealed record DailyBoundaryCandidate(
    MergedPilotStop Stop,
    double DistanceMeters,
    AdaptiveDistanceZone DistanceZone,
    int OverlapMinutes,
    bool Reliable,
    double ConfidenceScore,
    string Explanation);

internal sealed record DailyBoundarySelection(
    DailyBoundarySide Side,
    AdaptiveMatchDecision Decision,
    DailyBoundaryCandidate? Selected,
    IReadOnlyList<DailyBoundaryCandidate> Candidates,
    string Assessment,
    AdaptiveMatchResult? GeneralMatch,
    WorksiteSession? WorksiteSession = null)
{
    public bool IsReliable => Selected is not null &&
        Decision is AdaptiveMatchDecision.Confirmed or AdaptiveMatchDecision.Probable;
}

/// <summary>
/// Selects a day boundary from spatially plausible visits. This deliberately sits above the
/// general performance matcher: time ordering decides between multiple visits at one site.
/// </summary>
internal static class DailyBoundarySelector
{
    public static DailyBoundarySelection Select(
        DailyBoundarySide side,
        DateTimeOffset blockStart,
        DateTimeOffset blockEnd,
        PilotLocationResolution resolution,
        IReadOnlyList<PilotStop> stops,
        AdaptiveMatchResult? generalMatch,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var geocode = resolution.Geocoding.Primary;
        if (geocode is null)
        {
            return Empty(side, generalMatch, "Geen bruikbaar Plenion-coordinaat voor boundaryselectie.");
        }

        // Boundary candidate generation intentionally keeps stops with LocationContinuity=false.
        var visits = VisitCandidateBuilder.Build(stops, options, distanceCalculator, false)
            .Select(item => item.ToMergedPilotStop(options));
        var candidates = visits.Select(visit => BuildCandidate(
                visit,
                blockStart,
                blockEnd,
                geocode.Coordinate,
                generalMatch,
                options,
                distanceCalculator))
            .Where(item => item.DistanceMeters <= options.MaximumLearnedClusterDistanceMeters)
            .OrderBy(item => item.Stop.Arrival)
            .ThenBy(item => item.DistanceMeters)
            .ToArray();
        var reliable = candidates.Where(item => item.Reliable);
        var selected = side == DailyBoundarySide.First
            ? reliable.OrderBy(item => item.Stop.Arrival).ThenBy(item => item.DistanceMeters).FirstOrDefault()
            : reliable.OrderByDescending(item => item.Stop.Departure).ThenBy(item => item.DistanceMeters).FirstOrDefault();
        if (selected is null)
        {
            var reason = candidates.Length == 0
                ? "Geen PowerFleet-stop binnen 500 m van de Plenion-site."
                : $"{candidates.Length} ruimtelijke boundarykandidaat/kandidaten, maar geen met voldoende duur, overlap en afstandsbewijs.";
            return Empty(side, generalMatch, reason, candidates);
        }

        var decision = selected.DistanceZone == AdaptiveDistanceZone.Strong0To100 &&
                       resolution.Geocoding.Status == GeocodingStatus.Geocoded
            ? AdaptiveMatchDecision.Confirmed
            : AdaptiveMatchDecision.Probable;
        var orderReason = side == DailyBoundarySide.First
            ? "vroegste betrouwbare aankomst"
            : "laatste betrouwbare vertrek";
        return new DailyBoundarySelection(
            side,
            decision,
            selected,
            candidates,
            $"Boundaryselectie: {orderReason} op {selected.DistanceMeters:0.#} m; " +
            $"{selected.OverlapMinutes} min overlap; visit {selected.Stop.MergedStopId}.",
            generalMatch);
    }

    private static DailyBoundaryCandidate BuildCandidate(
        MergedPilotStop visit,
        DateTimeOffset blockStart,
        DateTimeOffset blockEnd,
        GeoCoordinate coordinate,
        AdaptiveMatchResult? generalMatch,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator)
    {
        var distance = distanceCalculator.DistanceMetres(
            coordinate,
            new GeoCoordinate((double)visit.Latitude!.Value, (double)visit.Longitude!.Value));
        var overlap = OverlapMinutes(blockStart, blockEnd, visit.Arrival, visit.Departure);
        var zone = distance <= options.StrongDistanceMeters
            ? AdaptiveDistanceZone.Strong0To100
            : distance <= options.ProbableDistanceMeters
                ? AdaptiveDistanceZone.Probable101To250
                : distance <= options.MaximumLearnedClusterDistanceMeters
                    ? AdaptiveDistanceZone.Learned251To500
                    : AdaptiveDistanceZone.Beyond500;
        var acceptedByGeneralMatcher = generalMatch is
        {
            Decision: AdaptiveMatchDecision.Confirmed or AdaptiveMatchDecision.Probable,
            Selected: not null,
        } && SameVisit(generalMatch.Selected.Stop, visit);
        var adequateDwell = visit.DurationMinutes >= options.MinimumStopDurationMinutes && !visit.IsPassThrough;
        var adequateOverlap = overlap >= options.MinimumOverlapMinutes;
        var reliableDistance = distance <= options.ProbableDistanceMeters || acceptedByGeneralMatcher;
        var reliable = adequateDwell && adequateOverlap && reliableDistance;
        var spatialScore = Math.Max(0, 100d * (1d - distance / options.MaximumLearnedClusterDistanceMeters));
        var overlapScore = Math.Min(100d, overlap * 100d / Math.Max(1d, options.RecoveryStrongOverlapMinutes));
        var confidence = Math.Round(spatialScore * .7 + overlapScore * .3, 1);
        return new DailyBoundaryCandidate(
            visit,
            Math.Round(distance, 1),
            zone,
            overlap,
            reliable,
            confidence,
            string.Create(CultureInfo.InvariantCulture,
                $"distance={distance:0.#}m ({zone}); overlap={overlap}m; duur={visit.DurationMinutes}m; " +
                $"continuity niet vereist; reliable={reliable}; generalAccepted={acceptedByGeneralMatcher}"));
    }

    private static bool SameVisit(MergedPilotStop left, MergedPilotStop right) =>
        left.SourceStopIds.Intersect(right.SourceStopIds, StringComparer.Ordinal).Any();

    private static int OverlapMinutes(
        DateTimeOffset leftStart,
        DateTimeOffset leftEnd,
        DateTimeOffset rightStart,
        DateTimeOffset rightEnd)
    {
        var start = leftStart > rightStart ? leftStart : rightStart;
        var end = leftEnd < rightEnd ? leftEnd : rightEnd;
        return end <= start ? 0 : (int)Math.Floor((end - start).TotalMinutes);
    }

    private static DailyBoundarySelection Empty(
        DailyBoundarySide side,
        AdaptiveMatchResult? generalMatch,
        string reason,
        IReadOnlyList<DailyBoundaryCandidate>? candidates = null) =>
        new(side, AdaptiveMatchDecision.Unresolved, null, candidates ?? [], reason, generalMatch);
}

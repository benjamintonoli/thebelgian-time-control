using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

/// <summary>
/// Shared ReviewCase mapping from benchmark-shaped evidence. Does not change matcher thresholds.
/// </summary>
internal static class ReviewCaseFactory
{
    public static ReviewCase FromBenchmarkCase(
        LocationMatchingBenchmarkCase item,
        IReadOnlyList<string> provenance,
        AdaptiveLocationMatchingOptions options,
        string matcherCommit,
        string configurationHash)
    {
        var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
        var visits = OfflineVisitMerge.Merge(item.Candidates, options);
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
        var addressByStop = item.Candidates
            .GroupBy(candidate => candidate.StopId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.Address)
                    .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)),
                StringComparer.Ordinal);

        var candidates = visits
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    item.Start,
                    item.End,
                    visit.Arrival,
                    visit.Departure);
                var startDev = (int)Math.Round(
                    (visit.Arrival - item.Start).TotalMinutes,
                    MidpointRounding.AwayFromZero);
                var endDev = (int)Math.Round(
                    (visit.Departure - item.End).TotalMinutes,
                    MidpointRounding.AwayFromZero);
                var address = visit.StopIds
                    .Select(id => addressByStop.GetValueOrDefault(id))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return new ReviewVisitCandidate(
                    VisitCandidateId: string.Join('/', visit.StopIds),
                    ConstituentStopIds: visit.StopIds,
                    Address: address,
                    Arrival: visit.Arrival,
                    Departure: visit.Departure,
                    DistanceMeters: visit.DistanceMeters,
                    OverlapMinutes: overlap,
                    OverlapPercent: 100d * overlap / performanceMinutes,
                    StartDeviationMinutes: startDev,
                    EndDeviationMinutes: endDev,
                    GeocodeQuality: item.GeocodeQuality.ToString());
            })
            .OrderByDescending(visit => visit.OverlapMinutes)
            .ThenBy(visit => visit.DistanceMeters ?? double.MaxValue)
            .ToArray();

        var status = ResolveStatus(item, prediction, candidates);
        ReviewVisitCandidate? proposed = null;
        if (prediction.Accepted && prediction.SourceStopIds.Count > 0)
        {
            var id = string.Join('/', prediction.SourceStopIds);
            proposed = candidates.FirstOrDefault(visit =>
                string.Equals(visit.VisitCandidateId, id, StringComparison.Ordinal));
            if (proposed is null && candidates.Length > 0)
            {
                proposed = candidates[0];
            }
        }

        var (startDeviation, endDeviation, maxDeviation) =
            SpotcheckPriorityCalculator.DeviationsForVisit(proposed, proposed is not null);

        var matcher = new MatcherAssessment(
            MatcherStatus: status,
            ProposedAcceptance: prediction.Accepted,
            ProposedVisit: proposed,
            CandidateVisits: candidates,
            MatchReason: BuildMatchReason(status, prediction, proposed, candidates),
            GeocodeQuality: item.GeocodeQuality,
            StartDeviationMinutes: startDeviation,
            EndDeviationMinutes: endDeviation,
            MaxDeviationMinutes: maxDeviation,
            MatcherCommit: matcherCommit,
            ConfigurationHash: configurationHash);

        var source = new SourceEvidence(
            PerformanceId: item.PerformanceId,
            Date: item.Date,
            Technician: item.Technician,
            PlenionStart: item.Start,
            PlenionEnd: item.End,
            PlenionAddress: item.PlenionAddress,
            ProjectContext: item.Lacleunik,
            BonContext: null,
            CustomerContext: null,
            PreviousPerformance: item.PreviousPerformance,
            NextPerformance: item.NextPerformance,
            Lacleunik: item.Lacleunik);

        var draft = new ReviewCase(
            Source: source,
            Matcher: matcher,
            Admin: new AdminDecision(AdminReviewDecisionRules.InitialReviewStatus()),
            Priority: SpotcheckPriorityCalculator.FromDeviationMinutes(maxDeviation),
            Category: ReviewWorkCategory.DataQuality,
            HasRecurringConfirmedPattern: false,
            SourceProvenance: provenance);
        return SpotcheckPriorityCalculator.WithDerivedFields(draft, recurringPattern: false);
    }

    private static string ResolveStatus(
        LocationMatchingBenchmarkCase item,
        OfflineHybridPredictor.Prediction prediction,
        ReviewVisitCandidate[] candidates)
    {
        if (prediction.Accepted)
        {
            return prediction.Decision;
        }

        if (item.ExistingMatchStatus.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return "Ambiguous";
        }

        if (AreTopCandidatesComparable(candidates))
        {
            return "Ambiguous";
        }

        return string.IsNullOrWhiteSpace(prediction.Decision) ? "Unresolved" : prediction.Decision;
    }

    private static bool AreTopCandidatesComparable(ReviewVisitCandidate[] candidates)
    {
        if (candidates.Length < 2)
        {
            return false;
        }

        var first = candidates[0];
        var second = candidates[1];
        var overlapClose = Math.Abs(first.OverlapMinutes - second.OverlapMinutes) <= 5;
        var distanceClose =
            first.DistanceMeters is { } d1 &&
            second.DistanceMeters is { } d2 &&
            Math.Abs(d1 - d2) <= 50;
        return overlapClose && (distanceClose || first.DistanceMeters is null || second.DistanceMeters is null);
    }

    private static string BuildMatchReason(
        string status,
        OfflineHybridPredictor.Prediction prediction,
        ReviewVisitCandidate? proposed,
        ReviewVisitCandidate[] candidates)
    {
        if (string.Equals(status, "Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return "Meerdere kandidaatbezoeken zijn vergelijkbaar sterk.";
        }

        if (prediction.Accepted && proposed is not null)
        {
            return prediction.UsedRecovery
                ? $"Waarschijnlijk bezoek op basis van overlap {proposed.OverlapMinutes} min."
                : "Voorgesteld bezoek op basis van afstand/overlap.";
        }

        if (candidates.Length == 0)
        {
            return "Geen kandidaatbezoeken; geen betrouwbare match.";
        }

        return "Geen acceptatie; handmatige review vereist.";
    }
}

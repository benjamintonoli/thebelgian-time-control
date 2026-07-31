using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

/// <summary>
/// Spotcheck priority and list ordering. Pure rules; no matching changes.
/// </summary>
public static class SpotcheckPriorityCalculator
{
    public static SpotcheckPriorityTier FromDeviationMinutes(int maxAbsDeviationMinutes)
    {
        var abs = Math.Abs(maxAbsDeviationMinutes);
        if (abs >= 30)
        {
            return SpotcheckPriorityTier.HighPriority;
        }

        if (abs >= 15)
        {
            return SpotcheckPriorityTier.IndividualException;
        }

        if (abs >= 5)
        {
            return SpotcheckPriorityTier.PatternRelevant;
        }

        return SpotcheckPriorityTier.Informational;
    }

    public static int MaxDeviationMinutes(int startDeviationMinutes, int endDeviationMinutes) =>
        Math.Max(Math.Abs(startDeviationMinutes), Math.Abs(endDeviationMinutes));

    /// <summary>
    /// Small positive time advantage: arrives after planned start or leaves before planned end
    /// by at most 3 minutes (informative band). Review context only — no wage/fraud conclusions.
    /// </summary>
    public static bool IsSmallTimeAdvantage(int startDeviationMinutes, int endDeviationMinutes)
    {
        var lateArrival = startDeviationMinutes > 0 && startDeviationMinutes <= 3;
        var earlyDeparture = endDeviationMinutes < 0 && endDeviationMinutes >= -3;
        return lateArrival || earlyDeparture;
    }

    public static IReadOnlySet<string> DetectRecurringSmallAdvantageTechnicians(
        IEnumerable<(string Technician, int StartDeviation, int EndDeviation)> rows,
        int minimumOccurrences = 3)
    {
        return rows
            .Where(item => IsSmallTimeAdvantage(item.StartDeviation, item.EndDeviation))
            .GroupBy(item => item.Technician, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= minimumOccurrences)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool NeedsHumanAttention(ReviewCase item) =>
        item.MatcherProposedAcceptance ||
        item.MatcherStatus is "Ambiguous" or "Unresolved" ||
        item.Priority is SpotcheckPriorityTier.IndividualException
            or SpotcheckPriorityTier.HighPriority ||
        item.RecurringSmallAdvantage;

    public static IReadOnlyList<ReviewCase> ApplyFilterAndSort(
        IReadOnlyList<ReviewCase> cases,
        AdminReviewFilter filter)
    {
        IEnumerable<ReviewCase> query = cases.Where(NeedsHumanAttention);

        if (!string.IsNullOrWhiteSpace(filter.Technician))
        {
            query = query.Where(item =>
                item.Technician.Contains(filter.Technician.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (filter.FromDate is { } from)
        {
            query = query.Where(item => item.Date >= from);
        }

        if (filter.ThroughDate is { } through)
        {
            query = query.Where(item => item.Date <= through);
        }

        if (filter.ReviewStatus is { } reviewStatus)
        {
            query = query.Where(item => item.ReviewStatus == reviewStatus);
        }

        if (!string.IsNullOrWhiteSpace(filter.MatcherStatus))
        {
            query = query.Where(item =>
                string.Equals(item.MatcherStatus, filter.MatcherStatus, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.MinimumDeviationMinutes is { } minDev)
        {
            query = query.Where(item => item.MaxDeviationMinutes >= minDev);
        }

        if (filter.HighPriorityOnly)
        {
            query = query.Where(item => item.Priority == SpotcheckPriorityTier.HighPriority);
        }

        if (filter.ProposedMatchesOnly)
        {
            query = query.Where(item => item.MatcherProposedAcceptance);
        }

        if (filter.AmbiguousOrUnresolvedOnly)
        {
            query = query.Where(item =>
                item.MatcherStatus is "Ambiguous" or "Unresolved");
        }

        return query
            .OrderBy(item => item.MaxDeviationMinutes >= 30 ? 0
                : item.MaxDeviationMinutes >= 15 ? 1
                : item.RecurringSmallAdvantage ? 2
                : 3)
            .ThenByDescending(item => item.MaxDeviationMinutes)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.PerformanceId)
            .ToArray();
    }
}

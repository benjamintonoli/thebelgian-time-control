using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

/// <summary>
/// Recurring pattern detection based solely on Confirmed admin decisions.
/// Pending / Unresolved / Ambiguous never form a pattern.
/// </summary>
public static class RecurringConfirmedPatternDetector
{
    public static IReadOnlySet<long> DetectPerformanceIds(IReadOnlyList<ReviewCase> cases)
    {
        var confirmed = cases
            .Where(item => item.ReviewStatus == AdminReviewStatus.Confirmed)
            .Select(item =>
            {
                var visit = SpotcheckPriorityCalculator.ResolveEffectiveVisit(item);
                if (visit is null || !SpotcheckPriorityCalculator.HasReliableVisitAnchor(item))
                {
                    return null;
                }

                var startAbs = Math.Abs(visit.StartDeviationMinutes);
                var endAbs = Math.Abs(visit.EndDeviationMinutes);
                if (Math.Max(startAbs, endAbs) < 5)
                {
                    return null;
                }

                var useStart = startAbs >= endAbs;
                var minutes = useStart ? visit.StartDeviationMinutes : visit.EndDeviationMinutes;
                if (Math.Abs(minutes) < 5)
                {
                    return null;
                }

                return new PatternRow(
                    item.PerformanceId,
                    item.Technician,
                    item.Date,
                    useStart ? PatternKind.Start : PatternKind.End,
                    minutes > 0 ? PatternDirection.Later : PatternDirection.Earlier);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        var result = new HashSet<long>();
        foreach (var group in confirmed.GroupBy(
                     item => (item.Technician.ToLowerInvariant(), item.Kind, item.Direction)))
        {
            var ordered = group.OrderBy(item => item.Date).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var window = new List<PatternRow> { ordered[i] };
                for (var j = i + 1; j < ordered.Length; j++)
                {
                    if (ordered[j].Date.DayNumber - ordered[i].Date.DayNumber > 30)
                    {
                        break;
                    }

                    window.Add(ordered[j]);
                }

                if (window.Count >= 3)
                {
                    foreach (var row in window)
                    {
                        result.Add(row.PerformanceId);
                    }
                }
            }
        }

        return result;
    }

    private enum PatternKind
    {
        Start,
        End,
    }

    private enum PatternDirection
    {
        Later,
        Earlier,
    }

    private sealed record PatternRow(
        long PerformanceId,
        string Technician,
        DateOnly Date,
        PatternKind Kind,
        PatternDirection Direction);
}

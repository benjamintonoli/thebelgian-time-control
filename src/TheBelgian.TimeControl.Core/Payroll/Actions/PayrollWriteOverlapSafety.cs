namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Strict write-safety overlap for Create/Adjust. Monthly review may stay tolerant;
/// mutation paths must not silently create positive paid overlap.
/// </summary>
public static class PayrollWriteOverlapSafety
{
    public const int SmallBoundaryMinutes = 5;

    public sealed record Interval(TimeSpan Start, TimeSpan End, long? PerformanceId = null);

    public sealed record OverlapDecision(
        int OverlapMinutes,
        bool IsAllowedTouch,
        bool RequiresBoundaryAlignment,
        bool BlocksWrite,
        TimeSpan? SuggestedEnd,
        string ExplanationNl);

    public static int OverlapMinutes(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        if (end <= start) return 0;
        return (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero);
    }

    public static OverlapDecision EvaluateCreateOrAdjust(
        TimeSpan proposedStart,
        TimeSpan proposedEnd,
        IReadOnlyList<Interval> otherSameDayIntervals)
    {
        var totalOverlap = 0;
        TimeSpan? nearestConflictStart = null;
        foreach (var other in otherSameDayIntervals)
        {
            var minutes = OverlapMinutes(proposedStart, proposedEnd, other.Start, other.End);
            if (minutes <= 0) continue;
            totalOverlap += minutes;
            if (nearestConflictStart is null || other.Start < nearestConflictStart)
                nearestConflictStart = other.Start;
        }

        if (totalOverlap == 0)
        {
            return new OverlapDecision(
                0,
                IsAllowedTouch: true,
                RequiresBoundaryAlignment: false,
                BlocksWrite: false,
                SuggestedEnd: null,
                "Geen dubbele geregistreerde tijd.");
        }

        // Boundary touch end==next start is already overlap=0.
        if (totalOverlap <= SmallBoundaryMinutes && nearestConflictStart is not null
            && proposedEnd > nearestConflictStart.Value
            && proposedStart < nearestConflictStart.Value)
        {
            var suggested = nearestConflictStart.Value;
            return new OverlapDecision(
                totalOverlap,
                IsAllowedTouch: false,
                RequiresBoundaryAlignment: true,
                BlocksWrite: true,
                SuggestedEnd: suggested,
                $"Overlap {totalOverlap} min: grens uitlijnen naar {suggested:hh\\:mm} verplicht vóór write.");
        }

        return new OverlapDecision(
            totalOverlap,
            IsAllowedTouch: false,
            RequiresBoundaryAlignment: false,
            BlocksWrite: true,
            SuggestedEnd: null,
            $"Overlap {totalOverlap} min: write geblokkeerd — nazicht vereist.");
    }
}

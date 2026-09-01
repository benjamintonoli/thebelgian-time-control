using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Reproduces Power BI legacy overlap (Dubbele uren) semantics for one resource/day.
/// Uses exact ATL supplied on input; CSV precision recovery belongs at the adapter boundary.
/// </summary>
public static class LegacyOverlapCalculator
{
    private static readonly HashSet<int> ExcludedHfdTaakIds = [10, 18, 23];

    public static IReadOnlyList<LegacyOverlapResult> Calculate(
        IReadOnlyList<LegacyOverlapPerformanceInput> rows)
    {
        var results = new List<LegacyOverlapResult>(rows.Count);
        var eligible = rows
            .Where(row => IsEligible(row))
            .OrderBy(row => row.Start)
            .ThenBy(row => row.SortKey)
            .ToList();

        foreach (var row in rows)
        {
            if (!IsEligible(row))
            {
                results.Add(new LegacyOverlapResult(
                    row.PerformanceId,
                    null,
                    0m,
                    Math.Max(0m, row.AtlHoursRaw),
                    0m));
                continue;
            }

            var predecessors = eligible
                .Where(candidate =>
                    candidate.Start < row.Start ||
                    (candidate.Start == row.Start && candidate.SortKey < row.SortKey))
                .ToList();

            DateTimeOffset? previousEndValue = predecessors.Count == 0
                ? null
                : predecessors.Max(candidate => candidate.End!.Value);

            var rawOverlapHours = CalculateRawOverlapHours(
                row.Start!.Value,
                row.End!.Value,
                previousEndValue);

            var maximumPayable = Math.Max(0m, row.AtlHoursRaw);
            var overlapHours = previousEndValue is null
                ? 0m
                : Math.Min(rawOverlapHours, maximumPayable);

            results.Add(new LegacyOverlapResult(
                row.PerformanceId,
                previousEndValue,
                rawOverlapHours,
                maximumPayable,
                overlapHours));
        }

        return results
            .OrderBy(result => result.PerformanceId)
            .ToList();
    }

    private static bool IsEligible(LegacyOverlapPerformanceInput row) =>
        row.Start is not null &&
        row.End is not null &&
        row.HfdTaakId is not null &&
        !ExcludedHfdTaakIds.Contains(row.HfdTaakId.Value);

    private static decimal CalculateRawOverlapHours(
        DateTimeOffset currentStart,
        DateTimeOffset currentEnd,
        DateTimeOffset? previousEnd)
    {
        if (previousEnd is null || previousEnd <= currentStart)
        {
            return 0m;
        }

        var overlapEnd = previousEnd.Value <= currentEnd ? previousEnd.Value : currentEnd;
        if (overlapEnd <= currentStart)
        {
            return 0m;
        }

        return (decimal)(overlapEnd - currentStart).TotalHours;
    }
}

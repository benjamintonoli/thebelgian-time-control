using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Payroll impact preview for overlap Adjust/Delete using the canonical legacy overlap calculator.
/// Never reports naive ATL delta as payroll impact.
/// </summary>
public static class OverlapPayrollImpactCalculator
{
    public sealed record Preview(
        decimal SourceHoursChange,
        decimal OverlapCorrectionBefore,
        decimal OverlapCorrectionAfter,
        decimal NetPayrollEffectHours,
        string SummaryNl);

    public static Preview ForDelete(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        long deletePerformanceId)
    {
        var before = SumOverlapCorrection(dayRows);
        var afterRows = dayRows.Where(item => item.SourceEntryId != deletePerformanceId).ToList();
        var after = SumOverlapCorrection(afterRows);
        var deleted = dayRows.FirstOrDefault(item => item.SourceEntryId == deletePerformanceId);
        var sourceChange = deleted is null ? 0m : -Math.Max(0m, deleted.AtlHoursRaw);
        var net = sourceChange + (before - after);
        return new Preview(
            Round(sourceChange),
            Round(before),
            Round(after),
            Round(net),
            $"Bron {Format(sourceChange)}; overlapcorrectie {Format(before)} → {Format(after)}; netto payroll {Format(net)}.");
    }

    public static Preview ForAdjust(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        long performanceId,
        DateTimeOffset newStart,
        DateTimeOffset newEnd)
    {
        var before = SumOverlapCorrection(dayRows);
        var afterRows = dayRows
            .Select(item =>
            {
                if (item.SourceEntryId != performanceId)
                {
                    return item;
                }

                var hours = Math.Max(0m, (decimal)(newEnd - newStart).TotalHours);
                return item with
                {
                    Start = newStart,
                    End = newEnd,
                    AtlHoursRaw = hours,
                    AtlMinutesExact = hours * 60m,
                    GrossClockDuration = newEnd - newStart,
                };
            })
            .ToList();
        var after = SumOverlapCorrection(afterRows);
        var original = dayRows.FirstOrDefault(item => item.SourceEntryId == performanceId);
        var newHours = Math.Max(0m, (decimal)(newEnd - newStart).TotalHours);
        var sourceChange = original is null ? 0m : newHours - Math.Max(0m, original.AtlHoursRaw);
        // Net payroll ≈ source ATL change minus change in overlap correction hours.
        var net = sourceChange + (before - after);
        return new Preview(
            Round(sourceChange),
            Round(before),
            Round(after),
            Round(net),
            $"Bron {Format(sourceChange)}; overlapcorrectie {Format(before)} → {Format(after)}; netto payroll {Format(net)}.");
    }

    public static Preview ForCreate(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        decimal proposedHours)
    {
        var before = SumOverlapCorrection(dayRows);
        // New row alone does not change existing overlap corrections until inserted;
        // expected actual += proposedHours; overlap correction on peers unchanged unless times collide.
        var net = Math.Max(0m, proposedHours);
        return new Preview(
            Round(proposedHours),
            Round(before),
            Round(before),
            Round(net),
            $"Nieuwe bron +{Format(proposedHours)}; overlapcorrectie ongewijzigd tot herberekening; verwacht effect ≈ {Format(net)}.");
    }

    private static decimal SumOverlapCorrection(IReadOnlyList<NormalizedPerformanceEntry> dayRows)
    {
        var inputs = dayRows
            .Where(item =>
                !item.IsCalendarSynthetic
                && item.Start is not null
                && item.End is not null)
            .Select(item => new LegacyOverlapPerformanceInput(
                item.SourceEntryId,
                item.SortKey,
                item.HfdTaakId,
                item.Start,
                item.End,
                item.AtlHoursRaw))
            .ToList();
        return LegacyOverlapCalculator.Calculate(inputs).Sum(item => item.OverlapHours);
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Format(decimal hours) =>
        hours.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("nl-BE")) + " u";
}

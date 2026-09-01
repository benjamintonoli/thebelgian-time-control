using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

/// <summary>
/// Maps current-source ledger rows to pure legacy daily calculator inputs.
/// Does not use historical CSV reconstruction or component overrides.
/// </summary>
public static class CurrentPayrollLegacyAdapter
{
    public static IReadOnlyList<LegacyDailyPerformanceInput> ToDailyInputs(CurrentPayrollLedger ledger)
    {
        var rows = new List<LegacyDailyPerformanceInput>(ledger.Performances.Count + ledger.SyntheticAbsences.Count);
        rows.AddRange(ledger.Performances.Select(ToDailyInputFromPerformance));
        rows.AddRange(ledger.SyntheticAbsences.Select(ToDailyInputFromSynthetic));
        return rows;
    }

    public static LegacyDailyPerformanceInput ToDailyInputFromPerformance(NormalizedPerformanceEntry entry) =>
        new(
            LegacySourceIdentity.ToCalculatorId(entry.SourceEntryKey),
            entry.SortKey,
            entry.HfdTaakId,
            entry.Start,
            entry.End,
            entry.AtlHoursRaw,
            entry.Pause.ExactMinutes.GetValueOrDefault() / 60m,
            entry.Km,
            entry.Date,
            entry.SourceEntryKey);

    public static LegacyDailyPerformanceInput ToDailyInputFromSynthetic(CalendarSyntheticEntry entry) =>
        new(
            LegacySourceIdentity.ToCalculatorId(entry.StableSourceKey),
            LegacySourceIdentity.ToCalculatorId(entry.StableSourceKey),
            entry.HfdTaakId,
            null,
            null,
            entry.SyntheticHoursRaw,
            0m,
            null,
            entry.Date,
            entry.StableSourceKey);
}

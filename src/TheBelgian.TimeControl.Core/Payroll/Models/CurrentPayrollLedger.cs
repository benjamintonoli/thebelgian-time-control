namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Current-source payroll ledger: real PROJ_Prest rows plus synthesized calendar absences.
/// No cross-dedupe between the two populations.
/// </summary>
public sealed record CurrentPayrollLedger(
    IReadOnlyList<NormalizedPerformanceEntry> Performances,
    IReadOnlyList<CalendarSyntheticEntry> SyntheticAbsences);

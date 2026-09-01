namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Optional day-level component overrides for historical CSV parity when exported
/// derived columns differ from pure recalculation (precision/duplicate-row semantics).
/// </summary>
public sealed record LegacyDailyComponentOverrides(
    decimal? TravelStartDeductionHours = null,
    decimal? TravelEndDeductionHours = null,
    decimal? PauseCorrectionHours = null);

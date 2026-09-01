namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Explicit source representation for PAUZE. Numeric values are never interpreted
/// without a declared kind (hours vs Excel day-fraction remain ambiguous otherwise).
/// </summary>
public enum PauseSourceKind
{
    /// <summary>Caller did not declare representation; numeric input is Invalid.</summary>
    Unspecified,

    /// <summary>Clock duration / time-of-day (e.g. 00:30:00 or Excel 1899-12-30 00:30:00).</summary>
    TimeOfDay,

    /// <summary>
    /// Excel/Power Query day fraction where hours = value × 24
    /// (historical Power Query: Number(PAUZE) * 24).
    /// </summary>
    ExcelDayFraction,

    /// <summary>Direct decimal hours (e.g. Plenion 0.75 → 45 minutes).</summary>
    Hours,
}

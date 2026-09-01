namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyDailyPauseResult(
    string ResourceId,
    DateOnly Date,
    decimal RegisteredPauseHours,
    bool HasOrdinaryWork,
    decimal PauseCorrectionHours);

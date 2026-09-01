namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyDailyPerformanceInput(
    long PerformanceId,
    long SortKey,
    int? HfdTaakId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHoursRaw,
    decimal PauseHoursRaw,
    decimal? Km,
    DateOnly Date);

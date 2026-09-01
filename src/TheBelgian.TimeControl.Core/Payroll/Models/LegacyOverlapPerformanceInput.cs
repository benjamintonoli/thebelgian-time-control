namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyOverlapPerformanceInput(
    long PerformanceId,
    long SortKey,
    int? HfdTaakId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHoursRaw,
    decimal PauseHoursRaw = 0m);

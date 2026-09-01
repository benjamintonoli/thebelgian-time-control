namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyTravelPerformanceInput(
    long PerformanceId,
    int? HfdTaakId,
    TimeSpan? VanTimeOfDay,
    decimal AtlHoursRaw,
    decimal? GrossHoursRaw = null);

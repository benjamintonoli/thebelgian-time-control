namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyOverlapResult(
    long PerformanceId,
    DateTimeOffset? PreviousEnd,
    decimal RawOverlapHours,
    decimal MaximumPayableOverlapHours,
    decimal OverlapHours);

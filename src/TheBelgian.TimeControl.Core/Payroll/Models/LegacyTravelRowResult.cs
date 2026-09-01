namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyTravelRowResult(
    long PerformanceId,
    bool IsDailyMinVan,
    bool IsDailyMaxVan,
    decimal TravelBeginHours,
    decimal TravelEndHours,
    decimal Extra15Hours);

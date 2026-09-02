namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Legacy Power BI KM-bedrag CJ components.
/// NetKmLegacyQuantity = EligibleKm - Extra75YtdHours (dimensional oddity preserved).
/// </summary>
public sealed record LegacyKmAllowanceResult(
    decimal EligibleKm,
    decimal Extra75RawKm,
    decimal Extra75YtdHours,
    decimal NetKmLegacyQuantity,
    decimal RatePerKm,
    decimal KmAmount);

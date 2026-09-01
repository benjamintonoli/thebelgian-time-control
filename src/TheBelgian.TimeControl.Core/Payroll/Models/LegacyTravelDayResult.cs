namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyTravelDayResult(
    string ResourceId,
    DateOnly Date,
    decimal TravelStartDeductionHours,
    decimal TravelEndDeductionHours,
    decimal Extra15TotalHours);

namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Effective-dated payroll eligibility decision. No national-register value is stored.
/// </summary>
public sealed record PayrollEmployeeConfiguration(
    string ResourceId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    PayrollEligibilityStatus EligibilityStatus,
    string ReasonCode,
    string? Comment,
    PayrollEligibilityDecisionSource DecisionSource)
{
    public bool IsActiveFor(DateOnly periodStart, DateOnly periodEnd)
    {
        if (ValidTo is not null && ValidFrom > ValidTo)
        {
            throw new InvalidOperationException(
                $"Ongeldige payrollconfiguratie voor resource {ResourceId}: ValidFrom ligt na ValidTo.");
        }

        var effectiveEnd = ValidTo ?? DateOnly.MaxValue;
        return ValidFrom <= periodEnd && effectiveEnd >= periodStart;
    }

    public bool Overlaps(PayrollEmployeeConfiguration other) =>
        string.Equals(ResourceId, other.ResourceId, StringComparison.Ordinal)
        && IsActiveFor(other.ValidFrom, other.ValidTo ?? DateOnly.MaxValue);
}

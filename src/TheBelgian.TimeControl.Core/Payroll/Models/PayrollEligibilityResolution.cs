namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record PayrollEligibilityResolution(
    PayrollEligibilityStatus EligibilityStatus,
    string? EligibilityReason,
    PayrollEligibilityStatus? SuggestedEligibility,
    string? SuggestedReason,
    bool HasExplicitConfiguration);

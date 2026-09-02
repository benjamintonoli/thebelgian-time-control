using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Eligibility;

public static class PayrollEligibilityResolver
{
    public static PayrollEligibilityResolution Resolve(
        PayrollEmployeeCandidate candidate,
        DateOnly periodStart,
        DateOnly periodEnd,
        IReadOnlyList<PayrollEmployeeConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(configurations);

        var active = configurations
            .Where(item => item.ResourceId == candidate.ResourceId && item.IsActiveFor(periodStart, periodEnd))
            .ToArray();
        if (active.Length > 1)
        {
            throw new InvalidOperationException(
                $"Overlappende payroll-eligibilityconfiguratie voor resource {candidate.ResourceId}.");
        }

        var suggestion = PayrollEligibilitySuggestionService.Suggest(candidate, periodStart);
        if (active.Length == 1)
        {
            var config = active[0];
            return new PayrollEligibilityResolution(
                config.EligibilityStatus,
                config.ReasonCode,
                suggestion.SuggestedEligibility,
                suggestion.SuggestedReason,
                true);
        }

        return new PayrollEligibilityResolution(
            PayrollEligibilityStatus.NeedsDecision,
            null,
            suggestion.SuggestedEligibility,
            suggestion.SuggestedReason,
            false);
    }

    public static void EnsureNoOverlap(
        IReadOnlyList<PayrollEmployeeConfiguration> existing,
        PayrollEmployeeConfiguration candidate)
    {
        foreach (var item in existing.Where(item => item.ResourceId == candidate.ResourceId))
        {
            if (Overlaps(item, candidate))
            {
                throw new InvalidOperationException(
                    $"Overlappende payroll-eligibilityconfiguratie voor resource {candidate.ResourceId}.");
            }
        }
    }

    private static bool Overlaps(PayrollEmployeeConfiguration left, PayrollEmployeeConfiguration right)
    {
        var leftEnd = left.ValidTo ?? DateOnly.MaxValue;
        var rightEnd = right.ValidTo ?? DateOnly.MaxValue;
        return left.ValidFrom <= rightEnd && right.ValidFrom <= leftEnd;
    }
}

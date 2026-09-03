using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Legacy;

/// <summary>
/// Deterministic auto-suggestion of payroll technician candidates for a period.
/// Explicit PayrollEmployeeConfiguration is applied separately and always wins for eligibility.
/// </summary>
public static class LegacyPayrollAutoCandidateSelector
{
    public static bool IsAutoCandidate(
        PayrollEmployeeCandidate candidate,
        DateOnly periodStart,
        IReadOnlySet<string> resourceIdsWithProjectLeiderTask23)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(resourceIdsWithProjectLeiderTask23);

        if (!candidate.IsActiveForPeriod(periodStart))
        {
            return false;
        }

        // Auto proposals represent probable payroll employees only (Present/Missing; never raw ID).
        if (candidate.AcertaIdentityStatus != AcertaIdentityStatus.Present)
        {
            return false;
        }

        if (LegacyPayrollNameMarkers.IsLegacyOaMarker(candidate.DisplayName))
        {
            return false;
        }

        if (LegacyPayrollNameMarkers.IsLegacyStagiairMarker(candidate.DisplayName))
        {
            return false;
        }

        if (LegacyPayrollTechnicianFunctions.IsTechnicianFunction(candidate.Function))
        {
            return true;
        }

        if (LegacyPayrollTechnicianFunctions.IsProjectLeider(candidate.Function)
            && resourceIdsWithProjectLeiderTask23.Contains(candidate.ResourceId))
        {
            return true;
        }

        return false;
    }

    public static IReadOnlyList<PayrollEmployeeCandidate> SelectAutoCandidates(
        IReadOnlyList<PayrollEmployeeCandidate> resources,
        DateOnly periodStart,
        IReadOnlySet<string> resourceIdsWithProjectLeiderTask23)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return resources
            .Where(item => IsAutoCandidate(item, periodStart, resourceIdsWithProjectLeiderTask23))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Period snapshot universe =
    /// (auto candidates EXCEPT effective Explicit Excluded) UNION effective Explicit Included.
    /// </summary>
    public static IReadOnlyList<PayrollEmployeeCandidate> SelectSnapshotCandidates(
        IReadOnlyList<PayrollEmployeeCandidate> resources,
        DateOnly periodStart,
        DateOnly periodEnd,
        IReadOnlySet<string> resourceIdsWithProjectLeiderTask23,
        IReadOnlyList<PayrollEmployeeConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(resourceIdsWithProjectLeiderTask23);
        ArgumentNullException.ThrowIfNull(configurations);

        var byId = resources.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        var selected = new Dictionary<string, PayrollEmployeeCandidate>(StringComparer.Ordinal);

        foreach (var auto in SelectAutoCandidates(resources, periodStart, resourceIdsWithProjectLeiderTask23))
        {
            selected[auto.ResourceId] = auto;
        }

        foreach (var config in configurations)
        {
            if (!config.IsActiveFor(periodStart, periodEnd))
            {
                continue;
            }

            if (config.EligibilityStatus == PayrollEligibilityStatus.Excluded)
            {
                selected.Remove(config.ResourceId);
                continue;
            }

            if (config.EligibilityStatus != PayrollEligibilityStatus.Included)
            {
                continue;
            }

            if (selected.ContainsKey(config.ResourceId))
            {
                continue;
            }

            if (byId.TryGetValue(config.ResourceId, out var resource))
            {
                selected[config.ResourceId] = resource;
            }
        }

        return selected.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToList();
    }
}

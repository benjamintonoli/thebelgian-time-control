using TheBelgian.TimeControl.Core.Payroll.Configuration;

namespace TheBelgian.TimeControl.Core.Payroll.Legacy;

/// <summary>
/// Pure legacy Extra-resource / performance-flow eligibility.
/// Mirrors Power BI: Extra resource = 0 keeps the row in the payroll performance flow.
/// </summary>
public static class LegacyPayrollPerformanceEligibility
{
    public const int ProjectLeiderIncludedHfdTaakId = 23;

    public static bool IsIncluded(string? function, int? hfdTaakId)
    {
        if (!LegacyPayrollTechnicianFunctions.IsProjectLeider(function))
        {
            return true;
        }

        return hfdTaakId == ProjectLeiderIncludedHfdTaakId;
    }
}

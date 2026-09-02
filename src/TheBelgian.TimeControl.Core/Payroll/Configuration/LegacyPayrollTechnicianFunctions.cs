namespace TheBelgian.TimeControl.Core.Payroll.Configuration;

/// <summary>
/// Legacy Power BI technician FUNCTIE set used only for auto-suggestion.
/// Explicit PayrollEmployeeConfiguration remains authoritative for membership.
/// Project Leider is intentionally NOT in this set (special candidacy + Extra-resource rules).
/// </summary>
public static class LegacyPayrollTechnicianFunctions
{
    public const string ProjectLeider = "Project Leider";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CCTV engineer",
        "CCTV Technieker",
        "Project Technician",
        "Project Technieker",
        "Service Technieker",
        "Technieker",
    };

    public static string? Normalize(string? function)
    {
        if (string.IsNullOrWhiteSpace(function))
        {
            return null;
        }

        return function.Trim();
    }

    public static bool IsTechnicianFunction(string? function)
    {
        var normalized = Normalize(function);
        return normalized is not null && All.Contains(normalized);
    }

    public static bool IsProjectLeider(string? function)
    {
        var normalized = Normalize(function);
        return string.Equals(normalized, ProjectLeider, StringComparison.OrdinalIgnoreCase);
    }
}

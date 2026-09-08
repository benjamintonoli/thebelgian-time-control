using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Activity types accepted by PlenionWriteService correction/delete contracts.
/// </summary>
public static class PayrollPwsSupportedActivities
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        nameof(PerformanceActivityType.CustomerWork),
        nameof(PerformanceActivityType.SiteWork),
        nameof(PerformanceActivityType.OfficeWork),
        nameof(PerformanceActivityType.WaitingTime),
    };

    public static bool IsSupported(string? activityType) =>
        !string.IsNullOrWhiteSpace(activityType) && Supported.Contains(activityType.Trim());
}

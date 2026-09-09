namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Restricted HFDTAAK ids an admin may choose for CREATE_PERFORMANCE when planning
/// did not prove activity. Excludes known non-job / non-payable create targets.
/// Final existence/active check remains in PWS.
/// </summary>
public static class PayrollCreateAllowedMainTasks
{
    private static readonly HashSet<int> Forbidden =
    [
        5,  // excluded by missing-tech credible job filter
        10, // overlap exclude (non-job)
        18, // overlap exclude (non-job)
        23, // waiting/standby — not a missing-tech job create target
    ];

    public static bool IsAllowed(int mainTaskId) =>
        mainTaskId > 0 && !Forbidden.Contains(mainTaskId);

    public static string RejectReason(int mainTaskId) =>
        mainTaskId <= 0
            ? "ACTIVITY_NOT_PROVEN: kies een geldige IDHFDTAAK."
            : $"ACTIVITY_NOT_ALLOWED: IDHFDTAAK {mainTaskId} is niet toegelaten voor create (geen wachttijd/reis/verboden taak).";
}

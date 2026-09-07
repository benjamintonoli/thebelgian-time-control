namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Canonical payroll activity-type strings used in PWS correction contracts.
/// </summary>
public static class PayrollStandbyActivityTypes
{
    public const string WaitingTime = "WaitingTime";
    public const long WaitingMainTaskExternalId = 23;

    public static string? FromMainTaskExternalId(long? mainTaskExternalId) =>
        mainTaskExternalId == WaitingMainTaskExternalId ? WaitingTime : null;

    public static bool IsWaitingTimePerformance(long? mainTaskExternalId, string? activityType) =>
        mainTaskExternalId == WaitingMainTaskExternalId
        && string.Equals(activityType, WaitingTime, StringComparison.Ordinal);

    public static string AdjustActionKey(string resourceId, DateOnly date, long performanceId) =>
        $"standby-adjust:{resourceId}:{date:yyyyMMdd}:{performanceId}";
}

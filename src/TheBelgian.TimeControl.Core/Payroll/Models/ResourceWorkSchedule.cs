namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Effective-dated contractual schedule. Durations are exact TimeSpan values
/// (e.g. 8h Mon–Thu, 7h Friday for standard full-time).
/// </summary>
public sealed record ResourceWorkSchedule(
    string WorkScheduleId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    TimeSpan Monday,
    TimeSpan Tuesday,
    TimeSpan Wednesday,
    TimeSpan Thursday,
    TimeSpan Friday,
    TimeSpan Saturday,
    TimeSpan Sunday)
{
    public static ResourceWorkSchedule StandardFullTime(
        string workScheduleId,
        DateOnly validFrom,
        DateOnly? validTo = null) =>
        new(
            workScheduleId,
            validFrom,
            validTo,
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(7),
            TimeSpan.Zero,
            TimeSpan.Zero);

    public TimeSpan ContractualDuration(DayOfWeek dayOfWeek) =>
        dayOfWeek switch
        {
            DayOfWeek.Monday => Monday,
            DayOfWeek.Tuesday => Tuesday,
            DayOfWeek.Wednesday => Wednesday,
            DayOfWeek.Thursday => Thursday,
            DayOfWeek.Friday => Friday,
            DayOfWeek.Saturday => Saturday,
            DayOfWeek.Sunday => Sunday,
            _ => TimeSpan.Zero,
        };
}

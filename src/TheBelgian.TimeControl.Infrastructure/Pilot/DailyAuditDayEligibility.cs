namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal enum DailyAuditDayStatus
{
    Eligible,
    ExcludedWeekend,
    ExcludedPublicHoliday,
    ExcludedLeave,
    ExcludedSickness,
}

internal sealed record DailyAuditDayEligibilityResult(
    DailyAuditDayStatus Status,
    string Reason)
{
    public bool IsEligible => Status == DailyAuditDayStatus.Eligible;
}

internal static class DailyAuditDayEligibility
{
    internal static readonly TimeOnly StandardWorkdayStart = new(8, 0);
    internal static readonly TimeOnly StandardWorkdayEnd = new(17, 0);

    public static DailyAuditDayEligibilityResult Evaluate(
        DateOnly date,
        IReadOnlyList<DailyAbsenceWindow> planning)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return new(DailyAuditDayStatus.ExcludedWeekend, "Weekend.");
        }

        var holiday = BelgianPublicHolidayCalendar.GetHolidayName(date);
        if (holiday is not null)
        {
            return new(
                DailyAuditDayStatus.ExcludedPublicHoliday,
                $"Belgische wettelijke feestdag: {holiday}.");
        }

        if (CoversStandardWorkday(planning, PlenionCalendarAbsenceKind.Sickness, date))
        {
            return new(
                DailyAuditDayStatus.ExcludedSickness,
                "Plenion-planning dekt de volledige standaardwerkdag met ziekte/afwezigheid.");
        }

        if (CoversStandardWorkday(planning, PlenionCalendarAbsenceKind.Leave, date))
        {
            return new(
                DailyAuditDayStatus.ExcludedLeave,
                "Plenion-planning dekt de volledige standaardwerkdag met verlof.");
        }

        return new(DailyAuditDayStatus.Eligible, "Werkdag is analyseerbaar.");
    }

    private static bool CoversStandardWorkday(
        IReadOnlyList<DailyAbsenceWindow> planning,
        PlenionCalendarAbsenceKind kind,
        DateOnly date)
    {
        var start = Local(date, StandardWorkdayStart);
        var end = Local(date, StandardWorkdayEnd);
        var cursor = start;
        foreach (var window in planning.Where(item => item.Kind == kind)
                     .OrderBy(item => item.Start))
        {
            if (window.End <= cursor)
            {
                continue;
            }

            if (window.Start > cursor)
            {
                return false;
            }

            cursor = window.End > cursor ? window.End : cursor;
            if (cursor >= end)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset Local(DateOnly date, TimeOnly time)
    {
        var value = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }
}

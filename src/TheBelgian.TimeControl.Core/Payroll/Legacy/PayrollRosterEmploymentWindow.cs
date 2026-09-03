namespace TheBelgian.TimeControl.Core.Payroll.Legacy;

/// <summary>
/// Payroll roster employment window: overtime/KM for a performance month are paid
/// the following month, so a resource remains auto-eligible through the full calendar
/// month after DATUMUITDIENST.
/// </summary>
public static class PayrollRosterEmploymentWindow
{
    /// <summary>
    /// Last date the resource remains auto-eligible for payroll roster/candidacy.
    /// Null when DATUMUITDIENST is null (no end).
    /// </summary>
    public static DateOnly? AutoEligibleThrough(DateOnly? employmentEndDate)
    {
        if (employmentEndDate is null)
        {
            return null;
        }

        var monthAfterDeparture = employmentEndDate.Value.AddMonths(1);
        return new DateOnly(
            monthAfterDeparture.Year,
            monthAfterDeparture.Month,
            DateTime.DaysInMonth(monthAfterDeparture.Year, monthAfterDeparture.Month));
    }

    /// <summary>
    /// True when the resource is still auto-eligible on <paramref name="referenceDate"/>
    /// (typically payroll period start / roster as-of date). Does not use DateTime.Today.
    /// </summary>
    public static bool IsAutoEligibleOn(DateOnly? employmentEndDate, DateOnly referenceDate)
    {
        var through = AutoEligibleThrough(employmentEndDate);
        return through is null || referenceDate <= through;
    }
}

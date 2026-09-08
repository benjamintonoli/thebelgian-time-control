using System.Globalization;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

/// <summary>
/// Display-only payroll formatting. Does not alter stored/calculated precision.
/// </summary>
public static class PayrollDisplayFormatting
{
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Hours(decimal? value) =>
        value is null ? "—" : value.Value.ToString("0.00", Belgian);

    public static string Hours(decimal value) =>
        value.ToString("0.00", Belgian);

    public static string Euro(decimal? value) =>
        value is null ? "—" : string.Create(Belgian, $"€ {value.Value:0.00}");

    public static string Euro(decimal value) =>
        string.Create(Belgian, $"€ {value:0.00}");

    /// <summary>
    /// Fixed Dutch weekday abbreviations for payroll review UX.
    /// Derived from the calendar date; independent of UI/server culture.
    /// </summary>
    public static string DutchWeekdayAbbreviation(DayOfWeek dayOfWeek) =>
        dayOfWeek switch
        {
            DayOfWeek.Monday => "ma",
            DayOfWeek.Tuesday => "di",
            DayOfWeek.Wednesday => "wo",
            DayOfWeek.Thursday => "do",
            DayOfWeek.Friday => "vr",
            DayOfWeek.Saturday => "za",
            DayOfWeek.Sunday => "zo",
            _ => throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, null),
        };

    public static string DutchWeekdayAbbreviation(DateOnly date) =>
        DutchWeekdayAbbreviation(date.DayOfWeek);

    public static string DutchWeekdayAbbreviation(DateTime date) =>
        DutchWeekdayAbbreviation(date.DayOfWeek);

    /// <summary>Queue-style: <c>ma 31/08</c>.</summary>
    public static string DateWithWeekdayShort(DateOnly date) =>
        $"{DutchWeekdayAbbreviation(date)} {date.ToString("dd/MM", Invariant)}";

    /// <summary>Detail/confirm/audit-style: <c>ma 31/08/2026</c>.</summary>
    public static string DateWithWeekday(DateOnly date) =>
        $"{DutchWeekdayAbbreviation(date)} {date.ToString("dd/MM/yyyy", Invariant)}";

    public static string DateWithWeekday(DateTime date) =>
        DateWithWeekday(DateOnly.FromDateTime(date));

    public static string DateWithWeekday(DateTimeOffset date) =>
        DateWithWeekday(DateOnly.FromDateTime(date.Date));
}

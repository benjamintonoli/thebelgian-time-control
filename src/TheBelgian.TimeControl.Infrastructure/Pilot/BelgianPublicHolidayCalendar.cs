namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class BelgianPublicHolidayCalendar
{
    public static bool IsPublicHoliday(DateOnly date) => GetHolidayName(date) is not null;

    public static string? GetHolidayName(DateOnly date)
    {
        var fixedHoliday = (date.Month, date.Day) switch
        {
            (1, 1) => "Nieuwjaar",
            (5, 1) => "Dag van de Arbeid",
            (7, 21) => "Nationale feestdag",
            (8, 15) => "Onze-Lieve-Vrouw-Hemelvaart",
            (11, 1) => "Allerheiligen",
            (11, 11) => "Wapenstilstand",
            (12, 25) => "Kerstmis",
            _ => null,
        };
        if (fixedHoliday is not null)
        {
            return fixedHoliday;
        }

        var easter = EasterSunday(date.Year);
        if (date == easter.AddDays(1)) return "Paasmaandag";
        if (date == easter.AddDays(39)) return "Onze-Lieve-Heer-Hemelvaart";
        if (date == easter.AddDays(50)) return "Pinkstermaandag";
        return null;
    }

    // Gregorian Meeus/Jones/Butcher computus; valid for the Gregorian calendar.
    internal static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}

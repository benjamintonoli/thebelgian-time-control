using System.Globalization;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

/// <summary>
/// Display-only payroll formatting. Does not alter stored/calculated precision.
/// </summary>
public static class PayrollDisplayFormatting
{
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static string Hours(decimal? value) =>
        value is null ? "—" : value.Value.ToString("0.00", Belgian);

    public static string Hours(decimal value) =>
        value.ToString("0.00", Belgian);

    public static string Euro(decimal? value) =>
        value is null ? "—" : string.Create(Belgian, $"€ {value.Value:0.00}");

    public static string Euro(decimal value) =>
        string.Create(Belgian, $"€ {value:0.00}");
}

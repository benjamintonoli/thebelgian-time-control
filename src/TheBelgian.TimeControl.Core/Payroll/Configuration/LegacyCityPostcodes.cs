namespace TheBelgian.TimeControl.Core.Payroll.Configuration;

public static class LegacyCityPostcodes
{
    public static IReadOnlySet<int> July2026Qualifying { get; } = new HashSet<int>
    {
        1000, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1081, 1082, 1083, 1090,
        1120, 1130, 1140, 1150, 1160, 1170, 1180, 1190, 1200, 1210, 2000, 2018,
        2020, 2030, 2040, 2050, 2060, 2100, 2110, 2140, 2150, 2170, 2530, 2540,
        2600, 2610, 2620, 2627, 2630, 2640, 2650, 2660, 2845, 9999,
    };
}

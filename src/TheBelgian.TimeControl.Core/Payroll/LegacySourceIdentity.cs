using System.Globalization;

namespace TheBelgian.TimeControl.Core.Payroll;

/// <summary>
/// Bridges stable textual source keys (e.g. KL148743_20260727_14) to calculator numeric ids.
/// Canonical identity remains the string key; numeric ids are for pure calculator dictionaries only.
/// </summary>
public static class LegacySourceIdentity
{
    public static string ForPerformance(long idProjPrest) =>
        idProjPrest.ToString(CultureInfo.InvariantCulture);

    public static string ForCalendarSynthetic(long idCalendar, DateOnly date, string resourceId) =>
        $"KL{idCalendar}_{date:yyyyMMdd}_{resourceId}";

    public static long ToCalculatorId(string sourceEntryKey)
    {
        if (long.TryParse(sourceEntryKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        return StableSyntheticKey(sourceEntryKey);
    }

    internal static long StableSyntheticKey(string value)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            return -(long)(hash & 0x7FFFFFFFFFFFFFFF);
        }
    }
}

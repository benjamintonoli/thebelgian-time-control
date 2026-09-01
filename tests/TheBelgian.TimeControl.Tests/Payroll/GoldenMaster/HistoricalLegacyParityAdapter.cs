using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public static class HistoricalLegacyParityAdapter
{
    public static LegacyOverlapPerformanceInput ToOverlapInput(
        PowerBiDetailRow row,
        long sortKey)
    {
        var date = row.Date ?? throw new InvalidDataException("DATUM ontbreekt.");
        var time = PerformanceTimeNormalizer.Normalize(date, row.VanRaw, row.TotRaw);
        var performanceId = ParsePerformanceId(row.PerformanceId);

        return new LegacyOverlapPerformanceInput(
            performanceId,
            sortKey,
            row.HfdTaakId,
            time.Start,
            time.End,
            row.AtlHours ?? 0m,
            ParsePauseHours(row.PauseRaw));
    }

    public static decimal ParsePauseHours(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0m;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime) ||
            DateTime.TryParse(
                raw,
                CultureInfo.GetCultureInfo("nl-BE"),
                DateTimeStyles.AllowWhiteSpaces,
                out dateTime))
        {
            return (decimal)dateTime.TimeOfDay.TotalHours;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var timeSpan)
            ? (decimal)timeSpan.TotalHours
            : 0m;
    }

    public static LegacyTravelPerformanceInput ToTravelInput(PowerBiDetailRow row)
    {
        return new LegacyTravelPerformanceInput(
            ParsePerformanceId(row.PerformanceId),
            row.HfdTaakId,
            ParseVanTimeOfDay(row.VanRaw),
            row.AtlHours ?? 0m,
            ComputeGrossHours(row.VanRaw, row.TotRaw));
    }

    public static decimal? ComputeGrossHours(string? vanRaw, string? totRaw)
    {
        var van = ParseVanTimeOfDay(vanRaw);
        var tot = ParseVanTimeOfDay(totRaw);
        if (van is null || tot is null)
        {
            return null;
        }

        var duration = tot.Value - van.Value;
        if (duration < TimeSpan.Zero)
        {
            duration += TimeSpan.FromDays(1);
        }

        return (decimal)duration.TotalHours;
    }

    public static long DefaultSortKey(PowerBiDetailRow row) =>
        ParsePerformanceId(row.PerformanceId);

    public static TimeSpan? ParseVanTimeOfDay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime) ||
            DateTime.TryParse(
                raw,
                CultureInfo.GetCultureInfo("nl-BE"),
                DateTimeStyles.AllowWhiteSpaces,
                out dateTime))
        {
            return dateTime.TimeOfDay;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var timeSpan)
            ? timeSpan
            : null;
    }

    public static long ParsePerformanceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidDataException("IDPROJ_PREST ontbreekt.");
        }

        var trimmed = raw.Trim().Trim('"');
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        // Calendar-synthetic Power BI rows (e.g. KL145730_20260702_19) use deterministic hash keys.
        return StableSyntheticKey(trimmed);
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

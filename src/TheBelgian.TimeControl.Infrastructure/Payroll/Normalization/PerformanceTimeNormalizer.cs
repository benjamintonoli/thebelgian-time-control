using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;

/// <summary>
/// Payroll-specific DATUM/VAN/TOT normalization. Does not modify Pilot readers.
/// Source values are Belgian local business clock times.
/// </summary>
public static class PerformanceTimeNormalizer
{
    private static readonly TimeZoneInfo Brussels =
        TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static PerformanceTimeNormalizationResult Normalize(
        DateOnly date,
        string? van,
        string? tot)
    {
        var start = ParseOptionalClock(date, van);
        var end = ParseOptionalClock(date, tot);

        if (start is not null && end is not null && end.Value < start.Value)
        {
            end = end.Value.AddDays(1);
        }

        TimeSpan? gross = null;
        if (start is not null && end is not null)
        {
            gross = end.Value - start.Value;
        }

        return new PerformanceTimeNormalizationResult(start, end, gross);
    }

    /// <summary>
    /// Normalizes ODBC CLR clock values (TimeSpan/DateTime) without stringifying first.
    /// </summary>
    public static PerformanceTimeNormalizationResult Normalize(
        DateOnly date,
        object? van,
        object? tot) =>
        Normalize(date, FormatClock(van), FormatClock(tot));

    public static decimal AtlMinutesExact(decimal atlHoursRaw) => atlHoursRaw * 60m;

    public static string? FormatClock(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return value switch
        {
            TimeSpan timeSpan => timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(),
        };
    }

    private static DateTimeOffset? ParseOptionalClock(DateOnly date, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (TryParseTimeOnly(trimmed, out var time))
        {
            return ToBrussels(date, time);
        }

        // Power BI / Excel export: 1899-12-30 HH:mm:ss — use time-of-day only.
        if (DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime) ||
            DateTime.TryParse(
                trimmed,
                Belgian,
                DateTimeStyles.AllowWhiteSpaces,
                out dateTime))
        {
            return ToBrussels(date, TimeOnly.FromDateTime(dateTime));
        }

        throw new FormatException($"Ongeldige kloktijd: '{trimmed}'.");
    }

    private static bool TryParseTimeOnly(string value, out TimeOnly time)
    {
        var formats = new[] { "HH:mm:ss", "H:mm:ss", "HH:mm", "H:mm" };
        return TimeOnly.TryParseExact(
                   value,
                   formats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out time) ||
               TimeOnly.TryParseExact(
                   value,
                   formats,
                   Belgian,
                   DateTimeStyles.None,
                   out time);
    }

    private static DateTimeOffset ToBrussels(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = Brussels.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}

public sealed record PerformanceTimeNormalizationResult(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    TimeSpan? GrossClockDuration);

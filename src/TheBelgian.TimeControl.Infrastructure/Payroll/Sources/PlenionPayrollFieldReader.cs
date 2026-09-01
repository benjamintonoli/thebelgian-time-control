using System.Globalization;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

internal static class PlenionPayrollFieldReader
{
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

    public static decimal ParseAtl(object value)
    {
        if (value is DBNull)
        {
            return 0m;
        }

        return value switch
        {
            decimal numeric => numeric,
            double numeric => Convert.ToDecimal(numeric, CultureInfo.InvariantCulture),
            float numeric => Convert.ToDecimal(numeric, CultureInfo.InvariantCulture),
            int numeric => numeric,
            long numeric => numeric,
            _ => decimal.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new InvalidDataException("ATL is niet numeriek."),
                NumberStyles.Number,
                CultureInfo.InvariantCulture),
        };
    }

    public static decimal? ParseOptionalDecimal(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return ParseAtl(value);
    }

    public static int? ParseOptionalInt(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static string? OptionalText(object? value) =>
        value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();

    public static DateOnly ParseDate(object value) => value switch
    {
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ when DateOnly.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed) => parsed,
        _ => throw new InvalidDataException("DATUM heeft een onbekend formaat."),
    };

    public static DateTime? ParseOptionalDateTime(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => dateTime,
            _ when DateTime.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) => parsed,
            _ => null,
        };
    }

    public static DateOnly? ParseOptionalDateOnly(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return ParseDate(value);
    }

    public static TimeOnly? ParseOptionalTimeOnly(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return value switch
        {
            TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
            DateTime dateTime => TimeOnly.FromDateTime(dateTime),
            _ when TimeOnly.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) => parsed,
            _ => null,
        };
    }

    public static string DescribeClrType(object? value) =>
        value is null or DBNull ? "null" : value.GetType().Name;
}

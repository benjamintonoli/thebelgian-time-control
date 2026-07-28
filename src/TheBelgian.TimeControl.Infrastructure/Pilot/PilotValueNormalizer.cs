using System.Globalization;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class PilotValueNormalizer
{
    private static readonly TimeZoneInfo Brussels =
        TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    public static DateTimeOffset ParseTimestamp(
        string date,
        string time,
        string field,
        ICollection<string> observations)
    {
        var completeTimestampFormats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd.MM.yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm",
        };
        foreach (var candidate in new[] { date.Trim(), time.Trim() })
        {
            if (DateTime.TryParseExact(
                    candidate,
                    completeTimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var completeTimestamp))
            {
                completeTimestamp = DateTime.SpecifyKind(
                    completeTimestamp,
                    DateTimeKind.Unspecified);
                observations.Add(
                    $"Powerfleet {field}date/{field}time: volledig timestampveld in Europe/Brussels.");
                return new DateTimeOffset(
                    completeTimestamp,
                    Brussels.GetUtcOffset(completeTimestamp));
            }
        }

        var combined = $"{date.Trim()} {time.Trim()}";
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd.MM.yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm",
            "yyyyMMdd HHmmss",
        };
        if (!DateTime.TryParseExact(
                combined,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed) &&
            !DateTime.TryParse(
                combined,
                CultureInfo.GetCultureInfo("nl-BE"),
                DateTimeStyles.AllowWhiteSpaces,
                out parsed))
        {
            throw new InvalidDataException($"Powerfleet {field}datum/-tijd is ongeldig.");
        }

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        observations.Add(
            $"Powerfleet {field}date/{field}time: XML-tekst in Europe/Brussels.");
        return new DateTimeOffset(parsed, Brussels.GetUtcOffset(parsed));
    }

    public static PilotDurationValue ParseDuration(
        string? value,
        TimeSpan elapsed,
        string field,
        ICollection<string> observations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            var fallback = CheckedMinutes(elapsed, field);
            observations.Add(
                $"Powerfleet {field}: ontbreekt; berekend uit start- en eindtijd.");
            return new PilotDurationValue(fallback, PilotNumericUnit.Calculated, "start/einde");
        }

        if (value.Contains(':', StringComparison.Ordinal) &&
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeSpan))
        {
            observations.Add($"Powerfleet {field}: tijdsduurtekst, als TimeSpan.");
            return new PilotDurationValue(
                CheckedMinutes(timeSpan, field),
                PilotNumericUnit.TimeSpan,
                "TimeSpan");
        }

        var numeric = ParseNumeric(value, field);
        var elapsedMinutes = elapsed.TotalMinutes;
        var candidates = new[]
        {
            new PilotDurationValue(
                checked((int)Math.Round(numeric, MidpointRounding.AwayFromZero)),
                PilotNumericUnit.Minutes,
                "numeriek als minuten"),
            new PilotDurationValue(
                checked((int)Math.Round(numeric / 60, MidpointRounding.AwayFromZero)),
                PilotNumericUnit.Seconds,
                "numeriek als seconden"),
            new PilotDurationValue(
                checked((int)Math.Round(numeric / 60000, MidpointRounding.AwayFromZero)),
                PilotNumericUnit.Milliseconds,
                "numeriek als milliseconden"),
        };
        var selected = candidates
            .Where(candidate => candidate.Minutes is >= 0 and <= 24 * 60)
            .OrderBy(candidate => Math.Abs(candidate.Minutes - elapsedMinutes))
            .First();
        if (Math.Abs(selected.Minutes - elapsedMinutes) > 2)
        {
            throw new InvalidDataException(
                $"Powerfleet {field} past niet bij het verschil tussen start en einde.");
        }

        observations.Add($"Powerfleet {field}: {selected.Strategy}, gevalideerd tegen start/einde.");
        return selected;
    }

    public static PilotOptionalDurationValue ParseOptionalDuration(
        string? value,
        PilotNumericUnit durationUnit,
        string field,
        ICollection<string> observations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new PilotOptionalDurationValue(null, "ontbreekt");
        }

        if (value.Contains(':', StringComparison.Ordinal) &&
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeSpan))
        {
            observations.Add($"Powerfleet {field}: tijdsduurtekst, als TimeSpan.");
            return new PilotOptionalDurationValue(CheckedMinutes(timeSpan, field), "TimeSpan");
        }

        var numeric = ParseNumeric(value, field);
        var minutes = durationUnit switch
        {
            PilotNumericUnit.Seconds =>
                checked((int)Math.Round(numeric / 60, MidpointRounding.AwayFromZero)),
            PilotNumericUnit.Milliseconds =>
                checked((int)Math.Round(numeric / 60000, MidpointRounding.AwayFromZero)),
            _ => checked((int)Math.Round(numeric, MidpointRounding.AwayFromZero)),
        };
        if (minutes is < 0 or > 7 * 24 * 60)
        {
            throw new InvalidDataException($"Powerfleet {field} is onwaarschijnlijk groot.");
        }

        var strategy = $"dezelfde numerieke eenheid als duration ({durationUnit})";
        observations.Add($"Powerfleet {field}: {strategy}.");
        return new PilotOptionalDurationValue(minutes, strategy);
    }

    public static PilotDistanceValue ParseDistance(
        string? value,
        ICollection<string> observations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            observations.Add("Powerfleet distance: ontbreekt; als 0 km.");
            return new PilotDistanceValue(0, "ontbreekt");
        }

        var normalized = value.Trim();
        var lower = normalized.ToLowerInvariant();
        var isKilometres = lower.EndsWith("km", StringComparison.Ordinal);
        var isMetres = !isKilometres && lower.EndsWith('m');
        normalized = normalized
            .Replace("km", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('m', 'M')
            .Trim();
        var numeric = ParseNumeric(normalized, "distance");

        if (isKilometres)
        {
            observations.Add("Powerfleet distance: expliciet km-suffix.");
            return new PilotDistanceValue(numeric, "expliciet kilometer");
        }

        if (isMetres)
        {
            observations.Add("Powerfleet distance: expliciet meter-suffix, gedeeld door 1000.");
            return new PilotDistanceValue(numeric / 1000, "expliciet meter");
        }

        observations.Add(
            "Powerfleet distance: numeriek zonder eenheid; voorlopig als kilometer geïnterpreteerd.");
        return new PilotDistanceValue(numeric, "numeriek zonder eenheid, aangenomen km");
    }

    private static decimal ParseNumeric(string value, string field)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ||
            decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.GetCultureInfo("nl-BE"),
                out result))
        {
            return result;
        }

        throw new InvalidDataException($"Powerfleet {field} is niet numeriek.");
    }

    private static int CheckedMinutes(TimeSpan value, string field)
    {
        var minutes = checked((int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes is >= 0 and <= 24 * 60
            ? minutes
            : throw new InvalidDataException($"Powerfleet {field} is onwaarschijnlijk groot.");
    }
}

internal enum PilotNumericUnit
{
    Calculated,
    TimeSpan,
    Minutes,
    Seconds,
    Milliseconds,
}

internal sealed record PilotDurationValue(
    int Minutes,
    PilotNumericUnit Unit,
    string Strategy);

internal sealed record PilotOptionalDurationValue(int? Minutes, string Strategy);

internal sealed record PilotDistanceValue(decimal Kilometres, string Strategy);

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Powerfleet;

public sealed class PowerfleetXmlParser
{
    public IReadOnlyList<PowerfleetTrip> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidDataException("Powerfleet gaf een leeg XML-antwoord.");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"Powerfleet XML is ongeldig op regel {exception.LineNumber}, positie {exception.LinePosition}.",
                exception);
        }

        var rows = document
            .Descendants()
            .Where(element =>
                Value(element, "tripid") is not null &&
                (Value(element, "startdate") is not null || Value(element, "starttime") is not null))
            .ToArray();

        if (rows.Length == 0)
        {
            throw new InvalidDataException("Powerfleet XML bevat geen herkenbare ritrecords.");
        }

        return rows.Select(ParseRow).ToArray();
    }

    private static PowerfleetTrip ParseRow(XElement row)
    {
        var tripId = Required(row, "tripid");
        var start = ParseTimestamp(row, "startdate", "starttime", tripId);
        var end = ParseTimestamp(row, "enddate", "endtime", tripId);
        var duration = ParseInt(Value(row, "duration"), "duration", tripId);
        if (duration == 0 && end >= start)
        {
            duration = (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero);
        }

        return new PowerfleetTrip
        {
            ExternalId = tripId,
            ObjectId = Value(row, "objectid"),
            ObjectName = Value(row, "objectname"),
            VehiclePlate = Value(row, "objectPlate"),
            DriverId = Value(row, "driverid"),
            DriverName = Value(row, "drivername"),
            Start = start,
            End = end,
            DurationMinutes = duration,
            DistanceKilometres = ParseDecimal(Value(row, "distance"), "distance", tripId),
            StartLocation = Value(row, "startlocation"),
            StartAddress = Value(row, "startaddress"),
            StartArea = Value(row, "startarea"),
            StartAreaGroup = Value(row, "startareagroup"),
            EndLocation = Value(row, "endlocation"),
            EndAddress = Value(row, "endaddress"),
            EndArea = Value(row, "endarea"),
            EndAreaGroup = Value(row, "endareagroup"),
            StoppedAfterMinutes = ParseNullableInt(
                Value(row, "stoppedafter"),
                "stoppedafter",
                tripId),
        };
    }

    private static DateTimeOffset ParseTimestamp(
        XElement row,
        string dateName,
        string timeName,
        string tripId)
    {
        var date = Required(row, dateName);
        var time = Required(row, timeName);
        var combined = $"{date} {time}";
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "yyyyMMdd HHmmss",
        };

        if (DateTimeOffset.TryParseExact(
                combined,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed) ||
            DateTimeOffset.TryParse(
                combined,
                CultureInfo.GetCultureInfo("nl-BE"),
                DateTimeStyles.AssumeLocal,
                out parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Powerfleet-rit {tripId} bevat een ongeldige {dateName}/{timeName}.");
    }

    private static int ParseInt(string? value, string field, string tripId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration))
        {
            return (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        }

        throw new InvalidDataException($"Powerfleet-rit {tripId} bevat een ongeldige {field}.");
    }

    private static decimal ParseDecimal(string? value, string field, string tripId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("nl-BE"), out parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"Powerfleet-rit {tripId} bevat een ongeldige {field}.");
    }

    private static int? ParseNullableInt(string? value, string field, string tripId) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseInt(value, field, tripId);

    private static string Required(XElement row, string name) =>
        Value(row, name)
        ?? throw new InvalidDataException($"Powerfleet-record mist het verplichte veld {name}.");

    private static string? Value(XElement row, string name)
    {
        var element = row.Elements().FirstOrDefault(child =>
            string.Equals(child.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        var attribute = row.Attributes().FirstOrDefault(item =>
            string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        var value = element?.Value ?? attribute?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

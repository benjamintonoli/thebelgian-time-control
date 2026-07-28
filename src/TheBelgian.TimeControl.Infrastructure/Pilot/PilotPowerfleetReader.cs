using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed class PilotPowerfleetReader(
    HttpClient httpClient,
    IOptions<PowerfleetOptions> options,
    ILogger<PilotPowerfleetReader> logger)
{
    private const int MaximumTrips = 200;
    private const int MaximumResponseBytes = 10 * 1024 * 1024;
    private readonly PowerfleetOptions _options = options.Value;

    public async Task<PowerfleetPilotReadResult> ReadAsync(
        ReadOnlyPilotRequest pilotRequest,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        var observations = new List<string>();
        var issues = new List<PilotIssue>();
        var definition = await ResolveEndpointAsync(observations, cancellationToken);
        var reportXml = await GetReportAsync(
            definition,
            pilotRequest,
            cancellationToken);
        var parsed = ParseReport(reportXml, observations, issues);
        VerifyServerFilters(
            parsed.NormalizedRecords,
            definition,
            pilotRequest,
            observations,
            issues);
        logger.LogInformation(
            "Read-only Powerfleet-pilot las {ReadCount} ritten; {RejectedCount} records afgewezen.",
            parsed.ReadCount,
            parsed.RejectedCount);
        return parsed with
        {
            Endpoint = definition.Endpoint.ToString(),
            FilterSummary = definition.FilterSummary,
            ServerSideFilterApplied = definition.AppliedFilterParameters.Count > 0,
            Observations = observations,
            Issues = issues,
        };
    }

    private async Task<PowerfleetReportDefinition> ResolveEndpointAsync(
        List<string> observations,
        CancellationToken cancellationToken)
    {
        foreach (var apiRoot in CandidateApiRoots())
        {
            var configurationEndpoint = new Uri(apiRoot, "Reports/getReportConf");
            var configurationUri = AddQuery(
                configurationEndpoint,
                new Dictionary<string, string?>
                {
                    ["id"] = _options.ReportId,
                    ["key"] = _options.ApiKey,
                });
            using var request = CreateRequest(configurationUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            EnsureSuccess(response, "rapportconfiguratie");
            var content = await ReadBoundedAsync(response, cancellationToken);
            var reportEndpoint = new Uri(apiRoot, "Reports/getReport");
            var definition = AnalyzeConfiguration(reportEndpoint, content, observations);
            observations.Add(
                $"Powerfleet API-pad gevalideerd via {configurationEndpoint.AbsolutePath}.");
            return definition;
        }

        throw new HttpRequestException(
            "Geen ondersteund read-only Powerfleet API-pad gevonden (/Api, services/Api of seeme/Api).");
    }

    private async Task<string> GetReportAsync(
        PowerfleetReportDefinition definition,
        ReadOnlyPilotRequest pilotRequest,
        CancellationToken cancellationToken)
    {
        var from = pilotRequest.FromDate.ToDateTime(TimeOnly.MinValue);
        var through = pilotRequest.ThroughDate.AddDays(1)
            .ToDateTime(TimeOnly.MinValue)
            .AddSeconds(-1);
        var values = new Dictionary<string, string?>
        {
            ["id"] = _options.ReportId,
            ["stateId"] = _options.StateId,
            ["parameters[begTimestamp]"] =
                from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["parameters[endTimestamp]"] =
                through.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["key"] = _options.ApiKey,
        };
        if (definition.ObjectFilterParameter is not null &&
            !string.IsNullOrWhiteSpace(pilotRequest.PowerfleetObjectId))
        {
            values[$"parameters[{definition.ObjectFilterParameter}]"] =
                pilotRequest.PowerfleetObjectId;
        }

        if (definition.DriverFilterParameter is not null &&
            !string.IsNullOrWhiteSpace(pilotRequest.PowerfleetDriverId))
        {
            values[$"parameters[{definition.DriverFilterParameter}]"] =
                pilotRequest.PowerfleetDriverId;
        }

        var reportUri = AddQuery(
            definition.Endpoint,
            values);
        using var request = CreateRequest(reportUri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureSuccess(response, "rapport");
        return await ReadBoundedAsync(response, cancellationToken);
    }

    private static PowerfleetPilotReadResult ParseReport(
        string xml,
        List<string> observations,
        List<PilotIssue> issues)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"Powerfleet-rapport is geen geldige XML (regel {exception.LineNumber}).",
                exception);
        }

        var apiError = document.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("errormessage", StringComparison.OrdinalIgnoreCase) ||
                element.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrWhiteSpace(apiError))
        {
            throw new InvalidDataException($"Powerfleet rapporteert: {apiError.Trim()}");
        }

        var rows = document.Descendants()
            .Where(element =>
                !element.Name.LocalName.Equals("tripid", StringComparison.OrdinalIgnoreCase) &&
                PilotXmlFields.Value(element, "tripid") is not null)
            .Where(element => !element.Elements().Any(child =>
                !child.Name.LocalName.Equals("tripid", StringComparison.OrdinalIgnoreCase) &&
                PilotXmlFields.Value(child, "tripid") is not null))
            .Take(MaximumTrips + 1)
            .ToArray();

        if (rows.Length == 0)
        {
            throw new InvalidDataException(
                "Powerfleet XML bevat geen herkenbare records met tripid.");
        }

        if (rows.Length > MaximumTrips)
        {
            issues.Add(new PilotIssue(
                "Powerfleet",
                null,
                "Onvoldoende gegevens",
                $"De pilotlimiet van {MaximumTrips} ritten is bereikt."));
        }

        var rawRecords = new List<PilotRawRecord>();
        var normalized = new List<NormalizedPilotTrip>();
        var rejected = 0;
        foreach (var row in rows.Take(MaximumTrips))
        {
            var raw = CreateRawRecord(row);
            rawRecords.Add(raw);
            try
            {
                normalized.Add(Normalize(row, observations));
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidDataException
                    or OverflowException or ArgumentOutOfRangeException)
            {
                rejected++;
                issues.Add(new PilotIssue(
                    "Powerfleet",
                    raw.SourceId,
                    "Parseprobleem",
                    exception.Message));
            }
        }

        return new PowerfleetPilotReadResult(
            null,
            "Filterselectie wordt na rapportconfiguratie ingevuld.",
            false,
            rawRecords,
            normalized,
            issues,
            observations,
            rawRecords.Count,
            rejected);
    }

    private static NormalizedPilotTrip Normalize(
        XElement row,
        List<string> observations)
    {
        var id = PilotXmlFields.Required(row, "tripid");
        var start = PilotValueNormalizer.ParseTimestamp(
            PilotXmlFields.Required(row, "startdate"),
            PilotXmlFields.Required(row, "starttime"),
            "start",
            observations);
        var end = PilotValueNormalizer.ParseTimestamp(
            PilotXmlFields.Required(row, "enddate"),
            PilotXmlFields.Required(row, "endtime"),
            "end",
            observations);
        if (end < start)
        {
            throw new InvalidDataException("Eindtijd ligt vóór starttijd.");
        }

        var elapsed = end - start;
        var duration = PilotValueNormalizer.ParseDuration(
            PilotXmlFields.Value(row, "duration"),
            elapsed,
            "duration",
            observations);
        var stoppedAfter = PilotValueNormalizer.ParseOptionalDuration(
            PilotXmlFields.Value(row, "stoppedafter"),
            duration.Unit,
            "stoppedafter",
            observations);
        var distance = PilotValueNormalizer.ParseDistance(
            PilotXmlFields.Value(row, "distance"),
            observations);
        var normalization =
            $"duration: {duration.Strategy}; stoppedafter: {stoppedAfter.Strategy}; " +
            $"distance: {distance.Strategy}";
        return new NormalizedPilotTrip(
            id,
            start,
            end,
            duration.Minutes,
            stoppedAfter.Minutes,
            distance.Kilometres,
            PilotXmlFields.Value(row, "driverid"),
            PilotXmlFields.Value(row, "drivername"),
            PilotXmlFields.Value(row, "objectid"),
            PilotXmlFields.Value(row, "objectname"),
            PilotXmlFields.Value(row, "objectPlate"),
            PilotXmlFields.Value(row, "startlocation"),
            PilotXmlFields.Value(row, "startaddress"),
            PilotXmlFields.Value(row, "startarea"),
            PilotXmlFields.Value(row, "startareagroup"),
            PilotXmlFields.Value(row, "endlocation"),
            PilotXmlFields.Value(row, "endaddress"),
            PilotXmlFields.Value(row, "endarea"),
            PilotXmlFields.Value(row, "endareagroup"),
            PilotXmlFields.OptionalDecimal(
                row,
                "startlatitude",
                "startlat",
                "startlocationlatitude"),
            PilotXmlFields.OptionalDecimal(
                row,
                "startlongitude",
                "startlon",
                "startlng",
                "startlocationlongitude"),
            PilotXmlFields.OptionalDecimal(
                row,
                "endlatitude",
                "endlat",
                "endlocationlatitude"),
            PilotXmlFields.OptionalDecimal(
                row,
                "endlongitude",
                "endlon",
                "endlng",
                "endlocationlongitude"),
            normalization);
    }

    private static PilotRawRecord CreateRawRecord(XElement row)
    {
        var fields = PowerfleetFieldNames.ToDictionary(
            field => field,
            field => new PilotRawValue(
                PilotXmlFields.Value(row, field),
                "XML-tekst"),
            StringComparer.OrdinalIgnoreCase);
        return new PilotRawRecord(
            fields["tripid"].Text ?? "(zonder tripid)",
            fields);
    }

    private IEnumerable<Uri> CandidateApiRoots()
    {
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (baseUri.AbsolutePath.Contains("/Api/", StringComparison.OrdinalIgnoreCase))
        {
            yield return baseUri;
            yield break;
        }

        yield return new Uri(baseUri, "Api/");
        yield return new Uri(baseUri, "services/Api/");
        yield return new Uri(baseUri, "seeme/Api/");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        return request;
    }

    private static Uri AddQuery(
        Uri endpoint,
        IReadOnlyDictionary<string, string?> values)
    {
        var query = string.Join(
            "&",
            values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}=" +
                Uri.EscapeDataString(pair.Value ?? string.Empty)));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Powerfleet {operation} gaf HTTP-status {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Powerfleet-response overschrijdt de pilotlimiet.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
            if (builder.Length > MaximumResponseBytes)
            {
                throw new InvalidDataException("Powerfleet-response overschrijdt de pilotlimiet.");
            }
        }

        return builder.ToString();
    }

    private static PowerfleetReportDefinition AnalyzeConfiguration(
        Uri endpoint,
        string content,
        List<string> observations)
    {
        try
        {
            var document = XDocument.Parse(content, LoadOptions.None);
            foreach (var field in new[] { "duration", "stoppedafter", "distance" })
            {
                var matches = document.Descendants()
                    .Where(element => element.Value.Contains(
                        field,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(element => element.Value.Trim())
                    .Where(value => value.Length is > 0 and <= 160)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();
                observations.Add(matches.Length == 0
                    ? $"Powerfleet rapportconfiguratie bevat geen leesbare metadata voor {field}."
                    : $"Powerfleet configuratie {field}: {string.Join(" | ", matches)}");
            }

            var parameterNames = document.Descendants()
                .Where(element =>
                    element.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                    element.Ancestors().Any(ancestor =>
                        ancestor.Name.LocalName.Equals(
                            "parameters",
                            StringComparison.OrdinalIgnoreCase)))
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            observations.Add(
                $"Powerfleet rapportparameters: {string.Join(", ", parameterNames)}.");

            var objectParameter = SelectFilterParameter(
                parameterNames,
                ["objects", "objectids", "objectid", "vehicles", "vehicleids", "vehicleid"],
                ["object", "vehicle"]);
            var driverParameter = SelectFilterParameter(
                parameterNames,
                ["drivers", "driverids", "driverid", "persons", "personids", "personid"],
                ["driver", "person"]);
            var applied = new List<string>();
            if (objectParameter is not null)
            {
                applied.Add($"object via parameters[{objectParameter}]");
            }

            if (driverParameter is not null)
            {
                applied.Add($"bestuurder via parameters[{driverParameter}]");
            }

            var summary = applied.Count == 0
                ? "Geen ondersteunde server-side object- of bestuurderparameter gevonden."
                : string.Join("; ", applied);
            observations.Add($"Powerfleet server-side filterselectie: {summary}");
            return new PowerfleetReportDefinition(
                endpoint,
                objectParameter,
                driverParameter,
                applied,
                summary);
        }
        catch (XmlException)
        {
            observations.Add(
                "Powerfleet rapportconfiguratie kon niet als XML worden geïnterpreteerd.");
            return new PowerfleetReportDefinition(
                endpoint,
                null,
                null,
                [],
                "Rapportconfiguratie kon niet worden gelezen; geen server-side filter toegepast.");
        }
    }

    private static string? SelectFilterParameter(
        IReadOnlyCollection<string> parameterNames,
        IReadOnlyList<string> exactPriorities,
        IReadOnlyList<string> fragments)
    {
        foreach (var priority in exactPriorities)
        {
            var exact = parameterNames.FirstOrDefault(name =>
                name.Equals(priority, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return parameterNames.FirstOrDefault(name =>
            fragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static void VerifyServerFilters(
        IReadOnlyCollection<NormalizedPilotTrip> records,
        PowerfleetReportDefinition definition,
        ReadOnlyPilotRequest request,
        List<string> observations,
        List<PilotIssue> issues)
    {
        if (definition.ObjectFilterParameter is not null &&
            !string.IsNullOrWhiteSpace(request.PowerfleetObjectId) &&
            records.Any(record =>
                !request.PowerfleetObjectId.Equals(
                    record.ObjectId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new PilotIssue(
                "Powerfleet",
                null,
                "Voertuigtoewijzing onzeker",
                "De server-side objectfilter leverde ook records van een ander object."));
        }

        if (definition.DriverFilterParameter is not null &&
            !string.IsNullOrWhiteSpace(request.PowerfleetDriverId) &&
            records.Any(record =>
                !request.PowerfleetDriverId.Equals(
                    record.DriverId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new PilotIssue(
                "Powerfleet",
                null,
                "Bestuurder ontbreekt",
                "De server-side bestuurderfilter leverde ook records van een andere bestuurder."));
        }

        if (definition.AppliedFilterParameters.Count > 0 &&
            !issues.Any(issue =>
                issue.Message.Contains(
                    "server-side",
                    StringComparison.OrdinalIgnoreCase)))
        {
            observations.Add(
                $"Powerfleet server-side filter geverifieerd op {records.Count} ontvangen records.");
        }
    }

    private void ValidateConfiguration()
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Powerfleet user-secrets zijn niet volledig ingesteld; er is geen aanvraag uitgevoerd.");
        }
    }

    private static readonly string[] PowerfleetFieldNames =
    [
        "tripid",
        "objectid",
        "objectname",
        "objectPlate",
        "driverid",
        "drivername",
        "duration",
        "distance",
        "stoppedafter",
        "startdate",
        "starttime",
        "startlocation",
        "startaddress",
        "startarea",
        "startareagroup",
        "enddate",
        "endtime",
        "endlocation",
        "endaddress",
        "endarea",
        "endareagroup",
        "startlatitude",
        "startlongitude",
        "endlatitude",
        "endlongitude",
    ];
}

internal sealed record PowerfleetPilotReadResult(
    string? Endpoint,
    string FilterSummary,
    bool ServerSideFilterApplied,
    IReadOnlyList<PilotRawRecord> RawRecords,
    IReadOnlyList<NormalizedPilotTrip> NormalizedRecords,
    IReadOnlyList<PilotIssue> Issues,
    IReadOnlyList<string> Observations,
    int ReadCount,
    int RejectedCount);

internal sealed record PowerfleetReportDefinition(
    Uri Endpoint,
    string? ObjectFilterParameter,
    string? DriverFilterParameter,
    IReadOnlyList<string> AppliedFilterParameters,
    string FilterSummary);

internal static class PilotXmlFields
{
    public static decimal? OptionalDecimal(
        XElement row,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = Value(row, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (decimal.TryParse(
                    value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var invariant) ||
                decimal.TryParse(
                    value,
                    NumberStyles.Number,
                    CultureInfo.GetCultureInfo("nl-BE"),
                    out invariant))
            {
                return invariant;
            }
        }

        return null;
    }

    public static string Required(XElement row, string name) =>
        Value(row, name)
        ?? throw new InvalidDataException($"Powerfleet-veld {name} ontbreekt.");

    public static string? Value(XElement row, string name)
    {
        var direct = row.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return Clean(direct.Value);
        }

        var attribute = row.Attributes().FirstOrDefault(item =>
            item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is not null)
        {
            return Clean(attribute.Value);
        }

        var named = row.Descendants().FirstOrDefault(element =>
            element.Attributes().Any(item =>
                item.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                item.Value.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (named is not null)
        {
            return Clean(named.Value);
        }

        var nameElement = row.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase) &&
            element.Value.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var valueElement = nameElement?.Parent?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase));
        return Clean(valueElement?.Value);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

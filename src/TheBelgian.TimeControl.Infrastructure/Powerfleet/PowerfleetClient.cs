using System.Net.Http.Headers;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Powerfleet;

public sealed class PowerfleetClient(
    HttpClient httpClient,
    IOptions<PowerfleetOptions> options,
    PowerfleetXmlParser parser,
    ILogger<PowerfleetClient> logger) : IPowerfleetClient
{
    private readonly PowerfleetOptions _options = options.Value;

    public async Task<IReadOnlyList<PowerfleetTrip>> GetTripsAsync(
        DateTimeOffset from,
        DateTimeOffset through,
        CancellationToken cancellationToken)
    {
        if (through < from)
        {
            throw new ArgumentException("Eindtijd ligt vóór begintijd.", nameof(through));
        }

        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Powerfleet is niet volledig geconfigureerd. Er is geen HTTP-aanroep uitgevoerd.");
        }

        var query = new Dictionary<string, string?>
        {
            ["reportId"] = _options.ReportId,
            ["stateId"] = _options.StateId,
            ["parameters[begTimestamp]"] = from.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["parameters[endTimestamp]"] = through.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        var separator = _options.BaseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var encodedQuery = string.Join(
            "&",
            query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
        var uri = _options.BaseUrl + separator + encodedQuery;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Powerfleet gaf HTTP-status {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        var trips = parser.Parse(xml);
        logger.LogInformation("{Count} Powerfleet-ritten opgehaald.", trips.Count);
        return trips;
    }
}

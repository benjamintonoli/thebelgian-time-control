using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

internal sealed class PowerfleetVehicleReader(
    HttpClient httpClient,
    IOptions<PowerfleetOptions> options)
{
    private readonly PowerfleetOptions _options = options.Value;

    public async Task<IReadOnlyList<PowerfleetVehicleObservation>> ReadAsync(
        CancellationToken cancellationToken)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PowerFleet Vehicles/get");
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("PowerFleet is niet volledig geconfigureerd.");
        }

        foreach (var root in CandidateApiRoots())
        {
            var endpoint = new Uri(root, "Vehicles/get");
            var uri = new UriBuilder(endpoint)
            {
                Query = "key=" + Uri.EscapeDataString(_options.ApiKey),
            }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = Parse(content);
            if (parsed.Count == 0)
            {
                throw new InvalidDataException("PowerFleet Vehicles/get bevat geen herkenbare voertuigen.");
            }
            return parsed;
        }

        throw new HttpRequestException("PowerFleet Vehicles/get werd op geen ondersteund API-pad gevonden.");
    }

    internal static IReadOnlyList<PowerfleetVehicleObservation> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        return content.TrimStart().StartsWith('<') ? ParseXml(content) : ParseJson(content);
    }

    private static PowerfleetVehicleObservation[] ParseJson(string content)
    {
        using var document = JsonDocument.Parse(content);
        var candidates = Descendants(document.RootElement)
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(ParseJsonObject)
            .Where(item => item is not null)
            .Cast<PowerfleetVehicleObservation>()
            .DistinctBy(item => item.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates;
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement element)
    {
        yield return element;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            foreach (var nested in Descendants(child)) yield return nested;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            foreach (var nested in Descendants(property.Value)) yield return nested;
        }
    }

    private static PowerfleetVehicleObservation? ParseJsonObject(JsonElement element)
    {
        var objectId = JsonValue(element, "objectId", "objectid", "id");
        var name = JsonValue(element, "name", "objectName", "objectname");
        if (string.IsNullOrWhiteSpace(objectId)) return null;
        return new(objectId.Trim(),
            JsonValue(element, "registrationPlate", "registrationplate", "plate", "licensePlate"),
            name?.Trim() ?? string.Empty, JsonValue(element, "make", "manufacturer"),
            JsonValue(element, "model"), JsonBool(element, true, "isActive", "active"));
    }

    private static string? JsonValue(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return property.Value.ToString();
            }
        }
        return null;
    }

    private static bool JsonBool(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return property.Value.GetBoolean();
            if (bool.TryParse(property.Value.ToString(), out var parsed)) return parsed;
        }
        return fallback;
    }

    private static PowerfleetVehicleObservation[] ParseXml(string content)
    {
        var document = XDocument.Parse(content, LoadOptions.None);
        return document.Descendants()
            .Where(element => element.Elements().Any())
            .Select(element =>
            {
                var objectId = XmlValue(element, "objectid", "objectId", "id");
                var name = XmlValue(element, "name", "objectname", "objectName");
                return string.IsNullOrWhiteSpace(objectId)
                    ? null
                    : new PowerfleetVehicleObservation(objectId.Trim(),
                        XmlValue(element, "registrationplate", "registrationPlate", "plate", "licensePlate"),
                        name?.Trim() ?? string.Empty, XmlValue(element, "make", "manufacturer"),
                        XmlValue(element, "model"),
                        !bool.TryParse(XmlValue(element, "active", "isActive"), out var active) || active);
            })
            .Where(item => item is not null)
            .Cast<PowerfleetVehicleObservation>()
            .DistinctBy(item => item.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? XmlValue(XElement element, params string[] names) =>
        element.Elements().FirstOrDefault(child =>
            names.Contains(child.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value;

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
}

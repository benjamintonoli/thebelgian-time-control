using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Resolves GPS points to configured known-location display labels via geofence.
/// Display/context only — never changes evidence confidence or payroll semantics.
/// </summary>
public sealed class KnownLocationCatalog
{
    public static KnownLocationCatalog Empty { get; } = new([]);

    private readonly KnownLocationDefinition[] _locations;
    private readonly IDistanceCalculator _distance;

    public KnownLocationCatalog(
        IEnumerable<KnownLocationDefinition> locations,
        IDistanceCalculator? distanceCalculator = null)
    {
        _locations = locations?.ToArray() ?? [];
        _distance = distanceCalculator ?? new HaversineDistanceCalculator();
    }

    public static KnownLocationCatalog FromOptions(
        KnownLocationsOptions? options,
        IDistanceCalculator? distanceCalculator = null)
    {
        if (options?.Locations is null || options.Locations.Count == 0)
        {
            return Empty;
        }

        var defs = options.Locations
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.RadiusMeters > 0)
            .Select(item => new KnownLocationDefinition(
                item.Name.Trim(),
                string.IsNullOrWhiteSpace(item.Locality) ? null : item.Locality.Trim(),
                item.Latitude,
                item.Longitude,
                item.RadiusMeters,
                string.IsNullOrWhiteSpace(item.SecondaryAddress) ? null : item.SecondaryAddress.Trim()))
            .ToArray();

        return new KnownLocationCatalog(defs, distanceCalculator);
    }

    public bool HasLocations => _locations.Length > 0;

    /// <summary>
    /// Resolve primary/secondary display for a GPS point. Coordinates win over address strings.
    /// </summary>
    public ResolvedKnownLocation Resolve(decimal? latitude, decimal? longitude, string? addressOrLabel)
    {
        if (latitude is not null && longitude is not null && _locations.Length > 0)
        {
            var point = new GeoCoordinate((double)latitude.Value, (double)longitude.Value);
            KnownLocationDefinition? best = null;
            var bestDistance = double.MaxValue;
            foreach (var location in _locations)
            {
                var metres = _distance.DistanceMetres(
                    new GeoCoordinate(location.Latitude, location.Longitude),
                    point);
                if (metres <= location.RadiusMeters && metres < bestDistance)
                {
                    best = location;
                    bestDistance = metres;
                }
            }

            if (best is not null)
            {
                var primary = string.IsNullOrWhiteSpace(best.Locality)
                    ? best.Name
                    : best.Name + " — " + best.Locality;
                var secondary = best.SecondaryAddress
                    ?? (string.IsNullOrWhiteSpace(addressOrLabel) ? null : addressOrLabel.Trim());
                return new ResolvedKnownLocation(primary, secondary, best.Name, MatchedByGeofence: true);
            }
        }

        var locality = PayrollProject300WorkbenchBuilder.ExtractLocality(addressOrLabel);
        var fallbackPrimary = locality
            ?? (string.IsNullOrWhiteSpace(addressOrLabel) ? null : addressOrLabel.Trim());
        var fallbackSecondary = string.IsNullOrWhiteSpace(addressOrLabel)
            || string.Equals(addressOrLabel.Trim(), fallbackPrimary, StringComparison.OrdinalIgnoreCase)
            ? null
            : addressOrLabel.Trim();

        return new ResolvedKnownLocation(fallbackPrimary, fallbackSecondary, AliasName: null, MatchedByGeofence: false);
    }
}

public sealed record KnownLocationDefinition(
    string Name,
    string? Locality,
    double Latitude,
    double Longitude,
    double RadiusMeters,
    string? SecondaryAddress);

public sealed record ResolvedKnownLocation(
    string? Primary,
    string? Secondary,
    string? AliasName,
    bool MatchedByGeofence);

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal enum DailyBoundaryEvidenceType { ExactSite, ContextSupported, Review, Unresolved, WorksiteSession }

internal sealed record DailyBoundaryContextLocation(
    string ExternalId,
    string Name,
    string Address,
    GeoCoordinate? Coordinate,
    string CustomerExternalId,
    bool StrongAddressMatch = false);

internal sealed record DailyBoundaryRawContextLocation(
    CustomerLocation Location,
    string CustomerExternalId);

internal sealed record DailyBoundaryContextIndex(
    IReadOnlyList<DailyBoundaryContextLocation> Locations,
    IReadOnlyList<PlenionWorkOrder> WorkOrders,
    IReadOnlyList<PlenionProject> Projects,
    IReadOnlyList<DailyBoundaryRawContextLocation>? RawLocations = null);

internal sealed record DailyBoundaryEvidence(
    DailyBoundarySide Side,
    DateTimeOffset PlenionBoundaryTime,
    DateTimeOffset? ExactSiteBoundaryTime,
    int RawExactSiteDeviationMinutes,
    DateTimeOffset? ContextBoundaryTime,
    string? ContextAddress,
    double? ContextDistanceMeters,
    string? ContextCustomerRelation,
    DailyBoundaryEvidenceType EvidenceType,
    DateTimeOffset? EffectiveBoundaryTime,
    int? EffectiveDeviationMinutes,
    int? PotentialDeviationMinutes,
    bool IsReliable,
    string Reason);

internal sealed record DailyContextLookupMetrics(
    TimeSpan Duration,
    int AddressMatchesWithoutGeocoding,
    int GeocodeCacheHits,
    int GeocodeCacheMisses,
    int ExternalGeocodeCalls,
    int UniquePlenionLocationsGeocoded,
    int NegativeCacheHits);

internal sealed partial class DailyBoundaryContextIndexProvider(
    IPlenionReader plenionReader,
    IGeocodingService geocodingService,
    LocationGeocodingCache geocodingCache)
{
    private readonly ConcurrentDictionary<string, GeocodingResult> _geocodes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ParsedWorkAddress> _parsedAddresses =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DailyBoundaryContextIndex> _lookupResults =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _externallyGeocoded = new(StringComparer.Ordinal);
    private readonly Stopwatch _duration = new();
    private int _addressMatches;
    private int _cacheHits;
    private int _cacheMisses;
    private int _externalCalls;
    private int _negativeCacheHits;

    [GeneratedRegex(@"(?<!\d)(\d{4})(?!\d)")]
    private static partial Regex PostalRegex();

    [GeneratedRegex(@"(?<street>[a-z][a-z0-9 ]*?)\s+(?<number>\d{1,4}[a-z]?)\b")]
    private static partial Regex StreetNumberRegex();

    public async Task<DailyBoundaryContextIndex> BuildAsync(CancellationToken cancellationToken)
    {
        var locations = await plenionReader.GetCustomerLocationsAsync(cancellationToken);
        var orders = await plenionReader.GetWorkOrdersAsync(cancellationToken);
        var projects = await plenionReader.GetProjectsAsync(cancellationToken);
        var ordersByLocation = orders.Where(order =>
                !string.IsNullOrWhiteSpace(order.DeliveryAddressExternalId) &&
                !string.IsNullOrWhiteSpace(order.CustomerExternalId))
            .GroupBy(order => order.DeliveryAddressExternalId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var raw = locations.Where(location =>
                !string.IsNullOrWhiteSpace(location.Address) && ordersByLocation.ContainsKey(location.ExternalId))
            .SelectMany(location => ordersByLocation[location.ExternalId]
                .Select(order => order.CustomerExternalId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(customerId => new DailyBoundaryRawContextLocation(location, customerId!)))
            .ToArray();
        return new DailyBoundaryContextIndex([], orders, projects, raw);
    }

    public async Task<DailyBoundaryContextIndex> ResolveAsync(
        DailyBoundaryContextIndex index,
        NormalizedPilotPerformance performance,
        string? contextAddress,
        bool allowExternalGeocoding,
        CancellationToken cancellationToken)
    {
        _duration.Start();
        try
        {
            return await ResolveCoreAsync(
                index, performance, contextAddress, allowExternalGeocoding, cancellationToken);
        }
        finally
        {
            _duration.Stop();
        }
    }

    public DailyContextLookupMetrics Metrics => new(
        _duration.Elapsed,
        _addressMatches,
        _cacheHits,
        _cacheMisses,
        _externalCalls,
        _externallyGeocoded.Count,
        _negativeCacheHits);

    private async Task<DailyBoundaryContextIndex> ResolveCoreAsync(
        DailyBoundaryContextIndex index,
        NormalizedPilotPerformance performance,
        string? contextAddress,
        bool allowExternalGeocoding,
        CancellationToken cancellationToken)
    {
        var postal = ExtractPostal(contextAddress);
        var customerIds = DailyBoundaryContextSelector.BoundaryCustomerIds(performance, index);
        if (postal is null || customerIds.Count == 0)
        {
            return index with { Locations = [] };
        }

        var lookupKey = string.Join('/', customerIds.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)) +
                        '|' + LocationGeocodingCache.NormalizeAddress(contextAddress!) +
                        '|' + allowExternalGeocoding;
        if (_lookupResults.TryGetValue(lookupKey, out var prior))
        {
            return prior;
        }

        var candidates = (index.RawLocations ?? []).Where(item =>
                customerIds.Contains(item.CustomerExternalId) &&
                string.Equals(ExtractPostal(item.Location.Address), postal, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var context = ParseAddress(contextAddress!);
        var addressMatches = candidates.Where(candidate =>
                StrongAddressMatch(context, ParseAddress(candidate.Location.Address!)))
            .Select(candidate => new DailyBoundaryContextLocation(
                candidate.Location.ExternalId,
                candidate.Location.Name,
                candidate.Location.Address!,
                null,
                candidate.CustomerExternalId,
                true))
            .ToArray();
        if (addressMatches.Length > 0)
        {
            Interlocked.Increment(ref _addressMatches);
            var addressResult = index with { Locations = addressMatches };
            _lookupResults[lookupKey] = addressResult;
            return addressResult;
        }

        var plausible = candidates.Select(candidate => new
            {
                Candidate = candidate,
                Parsed = ParseAddress(candidate.Location.Address!),
            })
            .Where(item => HasStreetTokenOverlap(context, item.Parsed))
            .OrderByDescending(item => SharedTokenCount(context, item.Parsed))
            .ThenBy(item => item.Candidate.Location.ExternalId, StringComparer.Ordinal)
            .Take(3)
            .Select(item => item.Candidate)
            .ToArray();
        foreach (var candidate in plausible)
        {
            var address = candidate.Location.Address!;
            var key = LocationGeocodingCache.NormalizeAddress(address);
            if (_geocodes.ContainsKey(key))
            {
                continue;
            }

            var cached = await geocodingCache.TryGetAsync(address, cancellationToken);
            if (cached.Found)
            {
                Interlocked.Increment(ref _cacheHits);
                _geocodes[key] = cached.Geocoding!;
                if (cached.Geocoding!.Status != GeocodingStatus.Geocoded ||
                    cached.Geocoding.Primary is null)
                {
                    Interlocked.Increment(ref _negativeCacheHits);
                }
                continue;
            }

            Interlocked.Increment(ref _cacheMisses);
            if (!allowExternalGeocoding)
            {
                continue;
            }

            Interlocked.Increment(ref _externalCalls);
            _externallyGeocoded.Add(key);
            _geocodes[key] = await geocodingService.GeocodeAsync(address, cancellationToken);
        }

        var resolved = new List<DailyBoundaryContextLocation>();
        foreach (var candidate in plausible)
        {
            var location = candidate.Location;
            var key = LocationGeocodingCache.NormalizeAddress(location.Address!);
            if (!_geocodes.TryGetValue(key, out var geocode) ||
                geocode.Status != GeocodingStatus.Geocoded ||
                geocode.Primary is null)
            {
                continue;
            }

            resolved.Add(new DailyBoundaryContextLocation(
                location.ExternalId,
                location.Name,
                location.Address!,
                geocode.Primary.Coordinate,
                candidate.CustomerExternalId));
        }

        var result = index with { Locations = resolved };
        _lookupResults[lookupKey] = result;
        return result;
    }

    private static string? ExtractPostal(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : PostalRegex().Match(value).Groups[1].Value is { Length: 4 } postal ? postal : null;

    private ParsedWorkAddress ParseAddress(string address)
    {
        var normalized = LocationGeocodingCache.NormalizeAddress(address)
            .Replace(" belgie", string.Empty, StringComparison.Ordinal)
            .Replace(" belgium", string.Empty, StringComparison.Ordinal);
        return _parsedAddresses.GetOrAdd(normalized, ParseNormalizedAddress);
    }

    internal static bool StrongAddressEquivalent(string left, string right) =>
        StrongAddressMatch(
            ParseNormalizedAddress(LocationGeocodingCache.NormalizeAddress(left)),
            ParseNormalizedAddress(LocationGeocodingCache.NormalizeAddress(right)));

    private static ParsedWorkAddress ParseNormalizedAddress(string value)
    {
        var pairs = StreetNumberRegex().Matches(value)
            .Select(match => new StreetNumber(
                NormalizeStreet(match.Groups["street"].Value),
                match.Groups["number"].Value))
            .Where(item => item.Street.Length >= 3)
            .ToArray();
        var postals = PostalRegex().Matches(value).Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4 && !token.All(char.IsDigit) && token is not "belgie" and not "belgium")
            .ToHashSet(StringComparer.Ordinal);
        return new ParsedWorkAddress(pairs, postals, tokens);
    }

    private static bool StrongAddressMatch(ParsedWorkAddress left, ParsedWorkAddress right) =>
        left.PostalCodes.Overlaps(right.PostalCodes) && left.StreetNumbers.Any(a =>
            right.StreetNumbers.Any(b => a.Number == b.Number && a.Street == b.Street));

    private static bool HasStreetTokenOverlap(ParsedWorkAddress left, ParsedWorkAddress right) =>
        left.PostalCodes.Overlaps(right.PostalCodes) && left.Tokens.Overlaps(right.Tokens);

    private static int SharedTokenCount(ParsedWorkAddress left, ParsedWorkAddress right) =>
        left.Tokens.Intersect(right.Tokens, StringComparer.Ordinal).Count();

    private static string NormalizeStreet(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not "be" and not "belgie" and not "belgium"));

    private sealed record ParsedWorkAddress(
        IReadOnlyList<StreetNumber> StreetNumbers,
        HashSet<string> PostalCodes,
        HashSet<string> Tokens);

    private sealed record StreetNumber(string Street, string Number);
}

internal static class DailyBoundaryContextSelector
{
    internal const double MaximumContextDistanceMeters = 100;
    internal const int MaximumBoundaryDifferenceMinutes = 5;
    private static readonly TimeSpan MaximumSequenceGap = TimeSpan.FromMinutes(60);

    public static DailyBoundaryEvidence Select(
        DailyBoundarySide side,
        BoundaryBlock block,
        NormalizedPilotPerformance performance,
        DailyBoundarySelection exact,
        IReadOnlyList<PilotStop> stops,
        DailyBoundaryContextIndex index,
        int minimumStopDurationMinutes,
        IDistanceCalculator distanceCalculator)
    {
        var plenionTime = side == DailyBoundarySide.First ? block.Start : block.End;
        var exactTime = side == DailyBoundarySide.First
            ? exact.Selected?.Stop.Arrival
            : exact.Selected?.Stop.Departure;
        var rawDeviation = exact.IsReliable && exactTime is not null
            ? PositiveDeviation(side, plenionTime, exactTime.Value)
            : 0;
        if (!exact.IsReliable || exactTime is null)
        {
            return new(
                side, plenionTime, exactTime, rawDeviation, null, null, null, null,
                DailyBoundaryEvidenceType.Unresolved, null, null, null, false,
                $"ExactSite unresolved: {exact.Assessment}");
        }

        if (rawDeviation <= MaximumBoundaryDifferenceMinutes)
        {
            return Exact(side, plenionTime, exactTime.Value, rawDeviation, exact);
        }

        var contextStop = AdjacentMeaningfulStop(side, exactTime.Value, stops, minimumStopDurationMinutes);
        if (contextStop is null || contextStop.Latitude is null || contextStop.Longitude is null)
        {
            return Exact(side, plenionTime, exactTime.Value, rawDeviation, exact,
                "Geen onmiddellijke contextstop met coördinaten.");
        }

        var customerIds = BoundaryCustomerIds(performance, index);
        if (customerIds.Count == 0)
        {
            return Exact(side, plenionTime, exactTime.Value, rawDeviation, exact,
                "Geen bewezen klant/debiteur-sleutel voor contextcontrole.");
        }

        var stopCoordinate = new GeoCoordinate(
            (double)contextStop.Latitude.Value,
            (double)contextStop.Longitude.Value);
        var match = index.Locations.Where(location => customerIds.Contains(location.CustomerExternalId))
            .Select(location => new ContextLocationEvidence(
                location,
                location.StrongAddressMatch || location.Coordinate is null
                    ? null
                    : distanceCalculator.DistanceMetres(stopCoordinate, location.Coordinate.Value)))
            .Where(item => item.Location.StrongAddressMatch ||
                           item.DistanceMeters <= MaximumContextDistanceMeters)
            .OrderByDescending(item => item.Location.StrongAddressMatch)
            .ThenBy(item => item.DistanceMeters)
            .FirstOrDefault();
        if (match is null)
        {
            return Exact(side, plenionTime, exactTime.Value, rawDeviation, exact,
                "Onmiddellijke stop is geen bewezen werklocatie van dezelfde klant binnen 100 m.");
        }

        var contextTime = side == DailyBoundarySide.First ? contextStop.Arrival : contextStop.Departure;
        var signedDifference = (contextTime - plenionTime).TotalMinutes;
        var absoluteDifference = Math.Abs(signedDifference);
        var relation = $"SameCustomerContext; klant/debiteur {match.Location.CustomerExternalId}; " +
                       $"LEVADR {match.Location.ExternalId} ({match.Location.Name})";
        var evidenceDescription = match.Location.StrongAddressMatch
            ? "sterke adresmatch (straat + huisnummer + postcode)"
            : $"{match.DistanceMeters:0.#} m";
        if (absoluteDifference > MaximumBoundaryDifferenceMinutes)
        {
            return new(
                side, plenionTime, exactTime, rawDeviation, contextTime, contextStop.Address,
                match.DistanceMeters is null ? null : Math.Round(match.DistanceMeters.Value, 1),
                relation, DailyBoundaryEvidenceType.Review,
                null, null, rawDeviation, false,
                $"SameCustomerContext via {evidenceDescription}, maar boundaryverschil " +
                $"{absoluteDifference:0.##} min is groter dan 5 min.");
        }

        return new(
            side, plenionTime, exactTime, rawDeviation, contextTime, contextStop.Address,
            match.DistanceMeters is null ? null : Math.Round(match.DistanceMeters.Value, 1),
            relation, DailyBoundaryEvidenceType.ContextSupported,
            contextTime, PositiveDeviation(side, plenionTime, contextTime), null, true,
            $"ContextSupported: onmiddellijke {(side == DailyBoundarySide.First ? "voorstop" : "nastop")} " +
            $"is bewezen SameCustomerContext via {evidenceDescription} en sluit binnen 5 min aan.");
    }

    private static DailyBoundaryEvidence Exact(
        DailyBoundarySide side,
        DateTimeOffset plenionTime,
        DateTimeOffset exactTime,
        int rawDeviation,
        DailyBoundarySelection exact,
        string? suffix = null) =>
        new(side, plenionTime, exactTime, rawDeviation, null, null, null, null,
            exact.WorksiteSession?.Confidence == WorksiteSessionConfidence.Strong
                ? DailyBoundaryEvidenceType.WorksiteSession
                : DailyBoundaryEvidenceType.ExactSite,
            exactTime, rawDeviation, null, true,
            string.Join(' ', new[] { exact.Assessment, suffix }
                .Where(value => !string.IsNullOrWhiteSpace(value))));

    private sealed record ContextLocationEvidence(
        DailyBoundaryContextLocation Location,
        double? DistanceMeters);

    internal static PilotStop? AdjacentMeaningfulStop(
        DailyBoundarySide side,
        DateTimeOffset exactTime,
        IReadOnlyList<PilotStop> stops,
        int minimumStopDurationMinutes)
    {
        var meaningful = stops.Where(stop =>
                stop.DurationMinutes >= minimumStopDurationMinutes &&
                stop.Latitude is not null && stop.Longitude is not null)
            .ToArray();
        return side == DailyBoundarySide.First
            ? meaningful.Where(stop => stop.Departure <= exactTime && exactTime - stop.Departure <= MaximumSequenceGap)
                .OrderByDescending(stop => stop.Departure).FirstOrDefault()
            : meaningful.Where(stop => stop.Arrival >= exactTime && stop.Arrival - exactTime <= MaximumSequenceGap)
                .OrderBy(stop => stop.Arrival).FirstOrDefault();
    }

    internal static HashSet<string> BoundaryCustomerIds(
        NormalizedPilotPerformance performance,
        DailyBoundaryContextIndex index)
    {
        var matchingOrders = index.WorkOrders.Where(order =>
                !string.IsNullOrWhiteSpace(performance.WorkOrderNumber) &&
                string.Equals(order.Number, performance.WorkOrderNumber, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var projectIds = matchingOrders.Select(order => order.ProjectExternalId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchingProjects = index.Projects.Where(project =>
                (!string.IsNullOrWhiteSpace(performance.ProjectNumber) &&
                 string.Equals(project.Number, performance.ProjectNumber, StringComparison.OrdinalIgnoreCase)) ||
                projectIds.Contains(project.ExternalId))
            .ToArray();
        return matchingOrders.Select(order => order.CustomerExternalId)
            .Concat(matchingProjects.Select(project => project.CustomerExternalId))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int PositiveDeviation(
        DailyBoundarySide side,
        DateTimeOffset plenionTime,
        DateTimeOffset gpsTime) =>
        HoursAuditService.PositiveWholeMinutes(side == DailyBoundarySide.First
            ? gpsTime - plenionTime
            : plenionTime - gpsTime);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Geocoding;

internal sealed class LocationGeocodingCache(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IGeocodingService geocodingService,
    TimeProvider timeProvider)
{
    public async Task<CachedGeocodingLookup> TryGetAsync(
        string originalAddress,
        CancellationToken cancellationToken)
    {
        var normalizedAddress = NormalizeAddress(originalAddress);
        var addressHash = Hash(normalizedAddress);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.LocationResolutionCacheEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.AddressHash == addressHash, cancellationToken);
        return existing is null
            ? new CachedGeocodingLookup(normalizedAddress, addressHash, false, null)
            : new CachedGeocodingLookup(normalizedAddress, addressHash, true, FromEntry(existing));
    }

    public async Task<CachedGeocodingResult> ResolveAsync(
        string? deliveryAddressExternalId,
        string originalAddress,
        CancellationToken cancellationToken)
    {
        var normalizedAddress = NormalizeAddress(originalAddress);
        var addressHash = Hash(normalizedAddress);
        await using var context = await contextFactory.CreateDbContextAsync(
            cancellationToken);
        var existing = await context.LocationResolutionCacheEntries
            .SingleOrDefaultAsync(
                item => item.AddressHash == addressHash,
                cancellationToken);
        if (existing is
            {
                Status: GeocodingStatus.Geocoded,
                Latitude: not null,
                Longitude: not null,
            })
        {
            return new CachedGeocodingResult(
                normalizedAddress,
                addressHash,
                FromEntry(existing));
        }

        var now = timeProvider.GetUtcNow();
        var result = await geocodingService.GeocodeAsync(
            originalAddress,
            cancellationToken);
        existing ??= new LocationResolutionCacheEntry
        {
            AddressHash = addressHash,
        };
        if (existing.Id == 0)
        {
            context.LocationResolutionCacheEntries.Add(existing);
        }

        existing.DeliveryAddressExternalId = deliveryAddressExternalId;
        existing.OriginalAddress = originalAddress;
        existing.NormalizedAddress = normalizedAddress;
        existing.Provider = result.Provider;
        existing.Status = result.Status;
        existing.Latitude = result.Primary?.Coordinate.Latitude;
        existing.Longitude = result.Primary?.Coordinate.Longitude;
        existing.ResolvedAddress = result.Primary?.FormattedAddress;
        existing.Confidence = result.Primary?.Confidence;
        existing.ErrorMessage = SafeError(result.ErrorMessage);
        existing.AlternativesJson = JsonSerializer.Serialize(
            result.Alternatives.Select(ToStoredCandidate));
        existing.LastAttemptAt = now;
        if (result.Status == GeocodingStatus.Geocoded)
        {
            existing.LastSuccessfulResolutionAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
        return new CachedGeocodingResult(
            normalizedAddress,
            addressHash,
            result);
    }

    internal static string NormalizeAddress(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) ==
                UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    internal static string Hash(string normalizedAddress) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedAddress)));

    private static GeocodingResult FromEntry(
        LocationResolutionCacheEntry entry)
    {
        var primary = entry.Latitude is not null && entry.Longitude is not null
            ? new GeocodingCandidate(
                new GeoCoordinate(entry.Latitude.Value, entry.Longitude.Value),
                entry.ResolvedAddress,
                entry.Confidence,
                null,
                [])
            : null;
        var alternatives = string.IsNullOrWhiteSpace(entry.AlternativesJson)
            ? []
            : JsonSerializer.Deserialize<StoredCandidate[]>(
                    entry.AlternativesJson)?
                .Select(FromStoredCandidate)
                .ToArray() ?? [];
        return new GeocodingResult(
            entry.Status,
            entry.Provider,
            primary,
            alternatives,
            entry.ErrorMessage,
            true);
    }

    private static StoredCandidate ToStoredCandidate(
        GeocodingCandidate candidate) =>
        new(
            candidate.Coordinate.Latitude,
            candidate.Coordinate.Longitude,
            candidate.FormattedAddress,
            candidate.Confidence,
            candidate.EntityType,
            candidate.MatchCodes.ToArray());

    private static GeocodingCandidate FromStoredCandidate(
        StoredCandidate candidate) =>
        new(
            new GeoCoordinate(candidate.Latitude, candidate.Longitude),
            candidate.FormattedAddress,
            candidate.Confidence,
            candidate.EntityType,
            candidate.MatchCodes);

    private static string? SafeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return error.Length <= 500 ? error : error[..500];
    }

    private sealed record StoredCandidate(
        double Latitude,
        double Longitude,
        string? FormattedAddress,
        string? Confidence,
        string? EntityType,
        string[] MatchCodes);
}

internal sealed record CachedGeocodingResult(
    string NormalizedAddress,
    string AddressHash,
    GeocodingResult Geocoding);

internal sealed record CachedGeocodingLookup(
    string NormalizedAddress,
    string AddressHash,
    bool Found,
    GeocodingResult? Geocoding);

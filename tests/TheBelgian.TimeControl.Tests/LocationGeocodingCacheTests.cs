using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests;

public sealed class LocationGeocodingCacheTests
{
    [Fact]
    public async Task Resolve_ReusesSuccessfulResultForSameAddressHash()
    {
        await using var fixture = await CacheFixture.CreateAsync();

        var first = await fixture.Cache.ResolveAsync(
            "address-1",
            "Teststraat 1, 9000 Gent",
            CancellationToken.None);
        var second = await fixture.Cache.ResolveAsync(
            "address-1",
            "  TESTSTRAAT 1, 9000 GENT  ",
            CancellationToken.None);

        Assert.Equal(first.AddressHash, second.AddressHash);
        Assert.Equal(1, fixture.Geocoder.CallCount);
        Assert.True(second.Geocoding.FromCache);
    }

    [Fact]
    public async Task Resolve_CallsProviderWhenAddressHashChanges()
    {
        await using var fixture = await CacheFixture.CreateAsync();

        var first = await fixture.Cache.ResolveAsync(
            "address-1",
            "Teststraat 1, 9000 Gent",
            CancellationToken.None);
        var second = await fixture.Cache.ResolveAsync(
            "address-1",
            "Teststraat 2, 9000 Gent",
            CancellationToken.None);

        Assert.NotEqual(first.AddressHash, second.AddressHash);
        Assert.Equal(2, fixture.Geocoder.CallCount);
    }

    private sealed class CacheFixture(
        SqliteConnection connection,
        CountingGeocoder geocoder,
        LocationGeocodingCache cache) : IAsyncDisposable
    {
        public CountingGeocoder Geocoder { get; } = geocoder;
        public LocationGeocodingCache Cache { get; } = cache;

        public static async Task<CacheFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TimeControlDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (var context = new TimeControlDbContext(options))
            {
                await context.Database.EnsureCreatedAsync();
            }

            var geocoder = new CountingGeocoder();
            return new CacheFixture(
                connection,
                geocoder,
                new LocationGeocodingCache(
                    new TestContextFactory(options),
                    geocoder,
                    TimeProvider.System));
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestContextFactory(
        DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);
    }

    private sealed class CountingGeocoder : IGeocodingService
    {
        public int CallCount { get; private set; }
        public bool IsConfigured => true;
        public string Provider => "Test";

        public Task<GeocodingResult> GeocodeAsync(
            string address,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new GeocodingResult(
                GeocodingStatus.Geocoded,
                Provider,
                new GeocodingCandidate(
                    new GeoCoordinate(51.05, 3.72),
                    address,
                    "High",
                    "Address",
                    ["Good"]),
                []));
        }
    }
}

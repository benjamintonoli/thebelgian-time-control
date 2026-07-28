using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class BroaderValidationPilotServiceTests
{
    [Fact]
    public void DiscoverDriverId_UsesDriverNameTokensAndIgnoresMissingDriver()
    {
        var technician = new Technician
        {
            ExternalId = "42",
            Code = "FD",
            Name = "Filip Dekuyper",
            Kind = 1,
        };
        var trips = new[]
        {
            Trip(null, "Filip Dekuyper", "ignored"),
            Trip("100", "Filip Dekuyper", "car-a"),
            Trip("100", "Filip Dekuyper", "car-b"),
            Trip("200", "Andere Persoon", "car-c"),
        };

        var driverId = BroaderValidationPilotService.DiscoverDriverId(trips, technician);

        Assert.Equal("100", driverId);
    }

    [Fact]
    public void DiscoverDriverId_ReturnsNullWhenNameDoesNotMatch()
    {
        var technician = new Technician
        {
            ExternalId = "42",
            Code = "FD",
            Name = "Filip Dekuyper",
            Kind = 1,
        };

        var driverId = BroaderValidationPilotService.DiscoverDriverId(
            [Trip("9", "Jonas Deklerck", "car")],
            technician);

        Assert.Null(driverId);
    }

    private static NormalizedPilotTrip Trip(
        string? driverId,
        string driverName,
        string objectId) =>
        new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Parse("2026-07-23T08:00:00+02:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-23T08:30:00+02:00", CultureInfo.InvariantCulture),
            30,
            10,
            5m,
            driverId,
            driverName,
            objectId,
            "Object",
            "1-ABC-123",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "test");
}

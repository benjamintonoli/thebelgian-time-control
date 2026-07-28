using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Tests;

public sealed class HaversineDistanceCalculatorTests
{
    private readonly HaversineDistanceCalculator _calculator = new();

    [Fact]
    public void DistanceMetres_ReturnsZeroForSameCoordinate()
    {
        var brussels = new GeoCoordinate(50.8503, 4.3517);

        Assert.Equal(0, _calculator.DistanceMetres(brussels, brussels), 6);
    }

    [Fact]
    public void DistanceMetres_IsApproximatelyBrusselsToAntwerp()
    {
        var distance = _calculator.DistanceMetres(
            new GeoCoordinate(50.8503, 4.3517),
            new GeoCoordinate(51.2194, 4.4025));

        Assert.InRange(distance, 40_000, 43_000);
    }

    [Fact]
    public void DistanceMetres_RejectsInvalidCoordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _calculator.DistanceMetres(
                new GeoCoordinate(91, 4),
                new GeoCoordinate(50, 4)));
    }
}

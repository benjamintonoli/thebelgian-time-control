using TheBelgian.TimeControl.Core.Interfaces;

namespace TheBelgian.TimeControl.Core.Services;

public sealed class HaversineDistanceCalculator : IDistanceCalculator
{
    private const double EarthRadiusMetres = 6_371_000;

    public double DistanceMetres(GeoCoordinate origin, GeoCoordinate destination)
    {
        Validate(origin);
        Validate(destination);

        var latitudeDelta = DegreesToRadians(destination.Latitude - origin.Latitude);
        var longitudeDelta = DegreesToRadians(destination.Longitude - origin.Longitude);
        var fromLatitude = DegreesToRadians(origin.Latitude);
        var toLatitude = DegreesToRadians(destination.Latitude);

        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(fromLatitude) * Math.Cos(toLatitude) *
                Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return EarthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static void Validate(GeoCoordinate coordinate)
    {
        if (coordinate.Latitude is < -90 or > 90 ||
            coordinate.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinate),
                "Coördinaten vallen buiten het geldige bereik.");
        }
    }
}

using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal static class DailyReviewTripContextMapper
{
    private static readonly TimeSpan BoundaryLinkTolerance = TimeSpan.FromMinutes(2);

    public static DailyReviewTripContext Map(
        string evidenceSnapshotJson,
        DailyReviewBoundaryEvidence first,
        DailyReviewBoundaryEvidence last)
    {
        using var document = JsonDocument.Parse(evidenceSnapshotJson);
        return Map(document.RootElement, first, last);
    }

    public static DailyReviewTripContext Map(
        JsonElement row,
        DailyReviewBoundaryEvidence first,
        DailyReviewBoundaryEvidence last)
    {
        if (!row.TryGetProperty("Trips", out var tripsElement) ||
            tripsElement.ValueKind != JsonValueKind.Array)
        {
            return new DailyReviewTripContext(null, null, []);
        }

        var rawTrips = tripsElement.EnumerateArray()
            .Select(Parse)
            .Where(item => item is not null)
            .Cast<RawTrip>()
            .OrderBy(item => item.Start)
            .ToArray();
        var firstTripId = BoundaryTrip(rawTrips, first.GpsTime, arrival: true)?.TripId;
        var lastTripId = BoundaryTrip(rawTrips, last.GpsTime, arrival: false)?.TripId;
        var trips = rawTrips.Select(item => new DailyReviewTrip(
                item.TripId,
                item.Start,
                item.End,
                item.StartAddress,
                item.EndAddress,
                item.DistanceKilometres ?? EstimatedDistance(item),
                item.DistanceKilometres is null && HasCoordinates(item),
                item.TripId == firstTripId,
                item.TripId == lastTripId))
            .ToArray();
        var before = trips.FirstOrDefault(item => item.IsFirstBoundaryArrivalTrip) ??
                     trips.LastOrDefault(item => item.End <= first.PlenionTime);
        var after = trips.FirstOrDefault(item => item.IsLastBoundaryDepartureTrip) ??
                    trips.FirstOrDefault(item => item.Start >= last.PlenionTime);
        return new DailyReviewTripContext(before, after, trips);
    }

    private static RawTrip? BoundaryTrip(
        IReadOnlyList<RawTrip> trips,
        DateTimeOffset? boundary,
        bool arrival)
    {
        if (boundary is null)
        {
            return null;
        }

        var candidate = trips
            .Select(item => new
            {
                Trip = item,
                Difference = (arrival ? item.End : item.Start) - boundary.Value,
            })
            .OrderBy(item => Math.Abs(item.Difference.TotalSeconds))
            .FirstOrDefault();
        return candidate is not null && candidate.Difference.Duration() <= BoundaryLinkTolerance
            ? candidate.Trip
            : null;
    }

    private static RawTrip? Parse(JsonElement trip)
    {
        if (!DateTimeOffset.TryParse(Text(trip, "Start"), CultureInfo.InvariantCulture, out var start) ||
            !DateTimeOffset.TryParse(Text(trip, "End"), CultureInfo.InvariantCulture, out var end))
        {
            return null;
        }

        return new RawTrip(
            Text(trip, "TripId") ?? $"trip-{start:O}",
            start,
            end,
            Text(trip, "StartLocation") ?? "Onbekend vertrekadres",
            Text(trip, "EndLocation") ?? "Onbekend aankomstadres",
            Number(trip, "DistanceKilometres"),
            Number(trip, "StartLatitude"),
            Number(trip, "StartLongitude"),
            Number(trip, "EndLatitude"),
            Number(trip, "EndLongitude"));
    }

    private static double? EstimatedDistance(RawTrip trip)
    {
        if (!HasCoordinates(trip))
        {
            return null;
        }

        const double earthRadiusKilometres = 6371;
        var startLatitude = DegreesToRadians(trip.StartLatitude!.Value);
        var endLatitude = DegreesToRadians(trip.EndLatitude!.Value);
        var latitudeDelta = endLatitude - startLatitude;
        var longitudeDelta = DegreesToRadians(
            trip.EndLongitude!.Value - trip.StartLongitude!.Value);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(startLatitude) * Math.Cos(endLatitude) *
                Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusKilometres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static bool HasCoordinates(RawTrip trip) =>
        trip.StartLatitude is not null && trip.StartLongitude is not null &&
        trip.EndLatitude is not null && trip.EndLongitude is not null;

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private sealed record RawTrip(
        string TripId,
        DateTimeOffset Start,
        DateTimeOffset End,
        string StartAddress,
        string EndAddress,
        double? DistanceKilometres,
        double? StartLatitude,
        double? StartLongitude,
        double? EndLatitude,
        double? EndLongitude);
}

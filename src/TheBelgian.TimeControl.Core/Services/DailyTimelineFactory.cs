using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

public static class DailyTimelineFactory
{
    private static readonly string[] TravelMarkers =
        ["verplaats", "rijtijd", "transport", "onderweg"];

    public static DailyTechnicianTimeline Create(
        Technician technician,
        DateOnly date,
        IEnumerable<PlenionPerformance> performances,
        IEnumerable<PowerfleetTrip> trips,
        bool hasCertainVehicleAssignment = true)
    {
        var dailyPerformances = performances
            .Where(performance =>
                performance.Date == date &&
                performance.TechnicianExternalId == technician.ExternalId)
            .OrderBy(performance => performance.Start)
            .ToArray();
        var dailyTrips = trips
            .Where(trip =>
                DateOnly.FromDateTime(trip.Start.LocalDateTime) == date &&
                (string.Equals(trip.DriverId, technician.ExternalId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(trip.DriverName, technician.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(trip => trip.Start)
            .ToArray();

        return new DailyTechnicianTimeline
        {
            TechnicianExternalId = technician.ExternalId,
            TechnicianName = technician.Name,
            Date = date,
            PlenionStart = dailyPerformances.FirstOrDefault()?.Start,
            PlenionEnd = dailyPerformances.LastOrDefault()?.End,
            RegisteredMinutes = dailyPerformances.Sum(DurationMinutes),
            BreakMinutes = dailyPerformances.Sum(performance => performance.BreakMinutes),
            RegisteredKilometres = dailyPerformances.Sum(performance => performance.Kilometres),
            RegisteredTravelMinutes = dailyPerformances
                .Where(IsTravelPerformance)
                .Sum(DurationMinutes),
            FirstTripStart = dailyTrips.FirstOrDefault()?.Start,
            LastTripEnd = dailyTrips.LastOrDefault()?.End,
            DrivingMinutes = dailyTrips.Sum(trip => trip.DurationMinutes),
            PowerfleetDistanceKilometres = dailyTrips.Sum(trip => trip.DistanceKilometres),
            HasCertainVehicleAssignment = hasCertainVehicleAssignment,
        };
    }

    public static bool IsTravelPerformance(PlenionPerformance performance) =>
        !string.IsNullOrWhiteSpace(performance.Description) &&
        TravelMarkers.Any(marker =>
            performance.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static int DurationMinutes(PlenionPerformance performance) =>
        Math.Max(0, (int)Math.Round(
            (performance.End - performance.Start).TotalMinutes,
            MidpointRounding.AwayFromZero) - performance.BreakMinutes);
}

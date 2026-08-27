using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed record PowerfleetVehicleStreamRisk(
    DateOnly Date,
    string Technician,
    int PhysicalStreamCount,
    IReadOnlyList<string> StreamIdentities,
    int OverlapMinutes,
    string Status,
    string Reason);

internal static class PowerfleetVehicleStreamIdentity
{
    public const string AmbiguousStatus = "AmbiguousVehicleAssignment";

    public static string? StableKey(NormalizedPilotTrip trip)
    {
        if (!string.IsNullOrWhiteSpace(trip.ObjectId))
        {
            return "object:" + trip.ObjectId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(trip.VehiclePlate))
        {
            return "plate:" + NormalizePlate(trip.VehiclePlate);
        }

        return null;
    }

    public static string ReconstructionKey(NormalizedPilotTrip trip) =>
        StableKey(trip) ?? "unidentified-driver:" + (trip.DriverId?.Trim() ?? "missing");

    public static string ObservationKey(NormalizedPilotTrip trip) =>
        ReconstructionKey(trip) + "|trip:" + trip.ExternalId;

    public static bool SamePhysicalStream(PilotStop left, PilotStop right)
    {
        if (!string.IsNullOrWhiteSpace(left.ObjectId) || !string.IsNullOrWhiteSpace(right.ObjectId))
        {
            return !string.IsNullOrWhiteSpace(left.ObjectId) &&
                   string.Equals(left.ObjectId, right.ObjectId, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(left.VehiclePlate) ||
            !string.IsNullOrWhiteSpace(right.VehiclePlate))
        {
            return !string.IsNullOrWhiteSpace(left.VehiclePlate) &&
                   !string.IsNullOrWhiteSpace(right.VehiclePlate) &&
                   string.Equals(
                       NormalizePlate(left.VehiclePlate),
                       NormalizePlate(right.VehiclePlate),
                       StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.DriverId, right.DriverId, StringComparison.OrdinalIgnoreCase);
    }

    public static PowerfleetVehicleStreamRisk Analyze(
        DateOnly date,
        string technician,
        IReadOnlyList<NormalizedPilotTrip> trips)
    {
        var dayTrips = trips.Where(item =>
                DateOnly.FromDateTime(item.StartDateTime.DateTime) == date)
            .ToArray();
        var streams = dayTrips.GroupBy(ReconstructionKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var streamWindows = streams.Select(group => new
            {
                group.Key,
                Start = group.Min(item => item.StartDateTime),
                End = group.Max(item => item.EndDateTime),
            })
            .ToArray();
        var overlapIntervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        for (var leftIndex = 0; leftIndex < streamWindows.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < streamWindows.Length; rightIndex++)
            {
                var left = streamWindows[leftIndex];
                var right = streamWindows[rightIndex];
                var start = left.Start > right.Start ? left.Start : right.Start;
                var end = left.End < right.End ? left.End : right.End;
                if (end > start)
                {
                    overlapIntervals.Add((start, end));
                }
            }
        }

        var overlapMinutes = UnionMinutes(overlapIntervals);
        var ambiguous = streams.Length > 1 && overlapMinutes > 0;
        var unidentified = streams.Any(group => group.Key.StartsWith(
            "unidentified-driver:", StringComparison.Ordinal));
        var status = ambiguous ? AmbiguousStatus : "SeparatedVehicleStreams";
        var reason = ambiguous
            ? $"{streams.Length} fysieke PowerFleet-streams hebben {overlapMinutes} minuten overlappende actieve vensters; " +
              "zonder expliciete geldige objecttoewijzing mag geen stream als persoonsbewijs worden gekozen."
            : streams.Length > 1
                ? $"{streams.Length} fysieke streams zonder tijdsoverlap; chronologisch per object gescheiden."
                : unidentified
                    ? "Geen ObjectId of kenteken beschikbaar; lineage blijft beperkt tot driver-ID."
                    : "Eén stabiele fysieke PowerFleet-stream.";
        return new PowerfleetVehicleStreamRisk(
            date,
            technician,
            streams.Length,
            streams.Select(group => Describe(group.First())).ToArray(),
            overlapMinutes,
            status,
            reason);
    }

    private static int UnionMinutes(List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0) return 0;
        var ordered = intervals.OrderBy(item => item.Start).ToArray();
        var start = ordered[0].Start;
        var end = ordered[0].End;
        var total = TimeSpan.Zero;
        foreach (var interval in ordered.Skip(1))
        {
            if (interval.Start <= end)
            {
                if (interval.End > end) end = interval.End;
                continue;
            }

            total += end - start;
            start = interval.Start;
            end = interval.End;
        }

        total += end - start;
        return (int)Math.Ceiling(total.TotalMinutes);
    }

    private static string Describe(NormalizedPilotTrip trip) =>
        $"{StableKey(trip) ?? "unidentified"}" +
        $"|plate={trip.VehiclePlate ?? "-"}|driver={trip.DriverId ?? "-"}";

    private static string NormalizePlate(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

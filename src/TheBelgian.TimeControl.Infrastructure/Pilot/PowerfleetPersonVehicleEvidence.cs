using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Day-specific PowerFleet person→vehicle evidence (DriverName context).
/// Never becomes a permanent primary identity; ObjectId assignment remains canonical.
/// </summary>
internal static class PowerfleetPersonVehicleEvidence
{
    public static bool ExactPersonName(string? driverName, string technicianName) =>
        string.Equals(
            NormalizePersonName(driverName),
            NormalizePersonName(technicianName),
            StringComparison.Ordinal);

    public static string NormalizePersonName(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty).Trim().ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string NormalizePlate(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// When canonical assignment is missing for a historical day, uniquely identify the
    /// ObjectId actually driven that day via exact DriverName match (context only).
    /// </summary>
    public static DaySpecificVehicleEvidence ResolveFromDriverNameTrips(
        string technicianName,
        IReadOnlyList<NormalizedPilotTrip> dayTrips)
    {
        var named = dayTrips
            .Where(item => ExactPersonName(item.DriverName, technicianName))
            .Where(item => !string.IsNullOrWhiteSpace(item.ObjectId))
            .ToArray();
        if (named.Length == 0)
        {
            return DaySpecificVehicleEvidence.None(
                "NO_POWERFLEET_DATA: geen DriverName-match op deze datum.");
        }

        var groups = named
            .GroupBy(item => item.ObjectId!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ToArray();
        if (groups.Length > 1)
        {
            return DaySpecificVehicleEvidence.Multiple(
                groups.Select(group => group.Key).ToArray(),
                "MULTIPLE_VEHICLES: meerdere ObjectId's via DriverName op dezelfde dag.");
        }

        var chosen = groups[0];
        var plate = chosen
            .Select(item => item.VehiclePlate)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var driverIds = chosen
            .Select(item => item.DriverId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return DaySpecificVehicleEvidence.Single(
            chosen.Key,
            plate,
            driverIds,
            chosen.Count(),
            "DaySpecificDriverEvidence: unieke ObjectId via exacte DriverName-match (context, niet permanent).");
    }

    public sealed record DaySpecificVehicleEvidence(
        bool HasUniqueObjectId,
        bool IsAmbiguous,
        string? ObjectId,
        string? RegistrationPlate,
        IReadOnlyList<string> DriverIds,
        int SupportingTripCount,
        string Reason)
    {
        public static DaySpecificVehicleEvidence None(string reason) =>
            new(false, false, null, null, [], 0, reason);

        public static DaySpecificVehicleEvidence Multiple(IReadOnlyList<string> objectIds, string reason) =>
            new(false, true, null, null, [], objectIds.Count, reason);

        public static DaySpecificVehicleEvidence Single(
            string objectId,
            string? plate,
            IReadOnlyList<string> driverIds,
            int tripCount,
            string reason) =>
            new(true, false, objectId, plate, driverIds, tripCount, reason);
    }
}

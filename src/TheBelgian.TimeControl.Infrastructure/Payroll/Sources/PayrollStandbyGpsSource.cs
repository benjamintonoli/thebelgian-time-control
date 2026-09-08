using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

/// <summary>
/// Batched read-only PowerFleet evidence for standby findings.
/// Canonical identity: <see cref="TechnicianVehicleAssignmentService.ResolveFromSnapshot"/>.
/// Day-specific fallback: exact DriverName→ObjectId evidence (context only).
/// </summary>
internal sealed class PayrollStandbyGpsSource(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    PilotPowerfleetReader powerfleetReader,
    ILogger<PayrollStandbyGpsSource> logger) : IPayrollStandbyGpsSource
{
    public int LastApiCallCount { get; private set; }

    public async Task<StandbyGpsBatchResult> ReadStandbyGpsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<(string ResourceId, string DisplayName, DateOnly Date)> standbyResourceDates,
        CancellationToken cancellationToken = default)
    {
        LastApiCallCount = 0;
        var pairs = standbyResourceDates
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ResourceId)
                && item.Date >= fromDate
                && item.Date <= throughDate)
            .Select(item => (
                ResourceId: item.ResourceId.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(item.DisplayName) ? item.ResourceId.Trim() : item.DisplayName.Trim(),
                item.Date))
            .Distinct()
            .ToArray();
        if (pairs.Length == 0)
        {
            return new StandbyGpsBatchResult([], 0, 0, 0, 0, 0, "No standby resource/dates.");
        }

        var resources = pairs.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
        var dates = pairs.Select(item => item.Date).Distinct().OrderBy(date => date).ToArray();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var assignments = await context.TechnicianVehicleAssignments.AsNoTracking()
            .Where(item => resources.Contains(item.TechnicianExternalId))
            .ToListAsync(cancellationToken);

        var tripsByDate = new Dictionary<DateOnly, List<NormalizedPilotTrip>>();
        foreach (var date in dates)
        {
            var daily = await powerfleetReader.ReadAsync(
                new ReadOnlyPilotRequest(
                    "payroll-standby-gps",
                    date,
                    date,
                    DriverOnlyLinking: true,
                    MaximumTrips: 1000),
                cancellationToken);
            LastApiCallCount++;
            tripsByDate[date] = daily.NormalizedRecords
                .DistinctBy(PowerfleetVehicleStreamIdentity.ObservationKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var days = new List<StandbyGpsDayEvidence>();
        var mappedResources = new HashSet<string>(StringComparer.Ordinal);
        var resourcesWithGps = new HashSet<string>(StringComparer.Ordinal);
        var canonicalMapped = 0;
        var daySpecificMapped = 0;
        var ambiguousCount = 0;

        foreach (var (resourceId, displayName, date) in pairs)
        {
            var at = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            var dayTrips = tripsByDate.GetValueOrDefault(date) ?? [];
            var canonical = TechnicianVehicleAssignmentService.ResolveFromSnapshot(
                assignments,
                resourceId,
                at);

            VehicleAssignmentResolution resolution;
            string mappingKind;
            string? unmappedReason = null;

            if (canonical.Status == VehicleAssignmentResolutionStatus.Resolved
                && !string.IsNullOrWhiteSpace(canonical.ObjectId))
            {
                resolution = canonical;
                mappingKind = "CanonicalAssignment";
                canonicalMapped++;
            }
            else if (canonical.Status == VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment)
            {
                resolution = canonical;
                mappingKind = "CanonicalAmbiguous";
                unmappedReason = "MULTIPLE_VEHICLES";
                ambiguousCount++;
            }
            else
            {
                var dayEvidence = PowerfleetPersonVehicleEvidence.ResolveFromDriverNameTrips(
                    displayName,
                    dayTrips);
                if (dayEvidence.HasUniqueObjectId && !string.IsNullOrWhiteSpace(dayEvidence.ObjectId))
                {
                    resolution = new VehicleAssignmentResolution(
                        VehicleAssignmentResolutionStatus.Resolved,
                        at,
                        resourceId,
                        dayEvidence.ObjectId,
                        [],
                        dayEvidence.Reason);
                    mappingKind = "DaySpecificDriverEvidence";
                    daySpecificMapped++;
                }
                else if (dayEvidence.IsAmbiguous)
                {
                    resolution = new VehicleAssignmentResolution(
                        VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment,
                        at,
                        resourceId,
                        null,
                        [],
                        dayEvidence.Reason);
                    mappingKind = "DaySpecificAmbiguous";
                    unmappedReason = "MULTIPLE_VEHICLES";
                    ambiguousCount++;
                }
                else
                {
                    resolution = canonical;
                    mappingKind = "None";
                    unmappedReason = dayTrips.Count == 0
                        ? "NO_POWERFLEET_DATA"
                        : "NO_RESOURCE_VEHICLE_ASSIGNMENT";
                }
            }

            var matched = ToTripEvidence(
                DailyHoursAuditService.TripsForAssignment(dayTrips, resolution));
            if (resolution.Status == VehicleAssignmentResolutionStatus.Resolved
                && !string.IsNullOrWhiteSpace(resolution.ObjectId))
            {
                mappedResources.Add(resourceId);
            }

            if (matched.Count > 0)
            {
                resourcesWithGps.Add(resourceId);
            }

            string? plate = null;
            if (resolution.Assignments.Count > 0)
            {
                plate = resolution.Assignments[0].RegistrationPlateSnapshot;
            }
            else if (mappingKind == "DaySpecificDriverEvidence")
            {
                plate = matched
                    .Select(item => item.VehiclePlate)
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            }

            days.Add(new StandbyGpsDayEvidence(
                ResourceId: resourceId,
                Date: date,
                HasVehicleMapping: resolution.Status == VehicleAssignmentResolutionStatus.Resolved
                    && !string.IsNullOrWhiteSpace(resolution.ObjectId),
                MappingAmbiguous: resolution.Status == VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment,
                ObjectId: resolution.ObjectId,
                RegistrationPlate: plate,
                MappingReason: resolution.Reason,
                Trips: matched,
                MappingKind: mappingKind,
                UnmappedReasonCode: unmappedReason));
        }

        var unmapped = resources.Count(id => !mappedResources.Contains(id));
        var withoutGps = resources.Count(id => !resourcesWithGps.Contains(id));
        var notes =
            $"Standby GPS batch dates={dates.Length}, pairs={pairs.Length}, apiCalls={LastApiCallCount}, "
            + $"mappedResources={mappedResources.Count} (canonicalDays={canonicalMapped}, daySpecificDays={daySpecificMapped}), "
            + $"unmapped={unmapped}, ambiguousDays={ambiguousCount}, "
            + $"withGps={resourcesWithGps.Count}, withoutGps={withoutGps}. "
            + "Canonical=TechnicianVehicleAssignmentService.ResolveFromSnapshot; "
            + "day-specific=exact DriverName→ObjectId context only; DriverId never permanent primary.";
        logger.LogInformation("Payroll standby GPS: {Notes}", notes);
        return new StandbyGpsBatchResult(
            days,
            LastApiCallCount,
            mappedResources.Count,
            unmapped,
            resourcesWithGps.Count,
            withoutGps,
            notes);
    }

    private static List<StandbyGpsTripEvidence> ToTripEvidence(IReadOnlyList<NormalizedPilotTrip> trips) =>
        trips
            .Select(item => new StandbyGpsTripEvidence(
                item.ExternalId,
                item.StartDateTime,
                item.EndDateTime,
                item.DistanceKilometres,
                item.DrivingMinutes,
                PreferLabel(item.StartAddress, item.StartLocation),
                PreferLabel(item.EndAddress, item.EndLocation),
                item.ObjectId,
                item.VehiclePlate,
                item.StartLatitude,
                item.StartLongitude,
                item.EndLatitude,
                item.EndLongitude))
            .ToList();

    private static string? PreferLabel(string? address, string? location) =>
        string.IsNullOrWhiteSpace(address) ? location : address;
}

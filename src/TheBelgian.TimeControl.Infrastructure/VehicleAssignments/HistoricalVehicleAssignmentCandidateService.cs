using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

public enum HistoricalVehicleCandidateStatus
{
    HighConfidenceCandidate,
    TransferSuspected,
    MultipleCandidates,
    NoCandidate,
    AlreadyConfirmed,
    NoTrackAndTrace,
}

public sealed record HistoricalVehicleAlternative(
    string ObjectId,
    string? RegistrationPlate,
    string? Name,
    int JulyTripDays,
    DateTimeOffset? FirstTripAt,
    DateTimeOffset? LastTripAt,
    IReadOnlyList<string> SupportingDriverIds);

public sealed record HistoricalVehicleAssignmentCandidate(
    string TechnicianExternalId,
    string Technician,
    string TechnicianCode,
    string? ProposedObjectId,
    string? RegistrationPlate,
    string? VehicleName,
    string? Make,
    string? Model,
    DateOnly From,
    DateOnly Through,
    HistoricalVehicleCandidateStatus Status,
    int JulyTripDays,
    int JulyTrips,
    int AuditableTechnicianDays,
    IReadOnlyList<string> SupportingDriverIds,
    IReadOnlyList<HistoricalVehicleAlternative> Alternatives,
    IReadOnlyList<string> Evidence,
    string CandidateKey);

public sealed record HistoricalVehicleCandidateResult(
    DateOnly From,
    DateOnly Through,
    int Technicians,
    int AlreadyConfirmed,
    int HighConfidenceCandidate,
    int TransferSuspected,
    int MultipleCandidates,
    int NoCandidate,
    int NoTrackAndTrace,
    int TheoreticallyAuditableDaysAfterHighConfidenceConfirmation,
    IReadOnlyList<HistoricalVehicleAssignmentCandidate> Candidates,
    DateTimeOffset GeneratedAt);

internal sealed class HistoricalVehicleAssignmentCandidateService(
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    PowerfleetVehicleReader vehicleReader,
    IDbContextFactory<TimeControlDbContext> contextFactory,
    TimeProvider timeProvider)
{
    public async Task<HistoricalVehicleCandidateResult> GenerateAsync(
        DateOnly from,
        DateOnly through,
        CancellationToken cancellationToken)
    {
        if (through < from) throw new ArgumentException("Einddatum ligt vóór begindatum.");
        var technicians = await plenionReader.ReadTechniciansWithPerformancesAsync(
            from, through, cancellationToken);
        var resourceIds = technicians.Select(item => item.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calendar = await plenionReader.ReadCalendarAbsencesAsync(
            resourceIds, from, through, cancellationToken);
        var auditableDays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var technician in technicians)
        {
            var scan = await plenionReader.ReadAsync(new ReadOnlyPilotRequest(
                technician.ExternalId, from, through, MaximumPerformances: 500), cancellationToken);
            var count = scan.NormalizedRecords.GroupBy(item => item.Date).Count(day =>
            {
                var windows = CalendarWindows(calendar.Where(item =>
                    item.ResourceExternalId.Equals(technician.ExternalId,
                        StringComparison.OrdinalIgnoreCase) &&
                    item.StartDate <= day.Key && item.EndDate >= day.Key), day.Key);
                if (!DailyAuditDayEligibility.Evaluate(day.Key, windows).IsEligible) return false;
                return DailyHoursAuditService.SelectLocationJobs(
                    day, technician.Name, windows).Length > 0;
            });
            auditableDays[technician.ExternalId] = count;
        }

        var allTrips = new List<NormalizedPilotTrip>();
        foreach (var date in Dates(from, through))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                BelgianPublicHolidayCalendar.IsPublicHoliday(date)) continue;
            var daily = await powerfleetReader.ReadAsync(new ReadOnlyPilotRequest(
                "historical-vehicle-candidates", date, date,
                DriverOnlyLinking: true, MaximumTrips: 1000), cancellationToken);
            allTrips.AddRange(daily.NormalizedRecords);
        }
        var trips = allTrips.DistinctBy(
                PowerfleetVehicleStreamIdentity.ObservationKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var vehicles = await vehicleReader.ReadAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var assignments = await context.TechnicianVehicleAssignments.AsNoTracking()
            .ToArrayAsync(cancellationToken);
        var physicalVehicles = await context.PhysicalVehicles.AsNoTracking()
            .ToArrayAsync(cancellationToken);
        var trackingEligibilities = await context.TechnicianTrackingEligibilities.AsNoTracking()
            .ToArrayAsync(cancellationToken);

        var auditPopulation = technicians.Where(item =>
                auditableDays.GetValueOrDefault(item.ExternalId) > 0)
            .ToArray();
        var candidates = auditPopulation.Select(technician => Classify(
                technician,
                vehicles,
                physicalVehicles,
                trips,
                assignments,
                auditableDays.GetValueOrDefault(technician.ExternalId),
                from,
                through,
                trackingEligibilities.SingleOrDefault(item =>
                    item.TechnicianExternalId.Equals(technician.ExternalId,
                        StringComparison.OrdinalIgnoreCase) &&
                    item.IsValidAt(LocalStart(from)))))
            .OrderBy(item => item.Status)
            .ThenBy(item => item.Technician, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(from, through, candidates.Length,
            candidates.Count(item => item.Status == HistoricalVehicleCandidateStatus.AlreadyConfirmed),
            candidates.Count(item => item.Status == HistoricalVehicleCandidateStatus.HighConfidenceCandidate),
            candidates.Count(item => item.Status == HistoricalVehicleCandidateStatus.TransferSuspected),
            candidates.Count(item => item.Status == HistoricalVehicleCandidateStatus.MultipleCandidates),
            candidates.Count(item => item.Status == HistoricalVehicleCandidateStatus.NoCandidate),
            candidates.Count(item => item.Status == HistoricalVehicleCandidateStatus.NoTrackAndTrace),
            candidates.Where(item => item.Status == HistoricalVehicleCandidateStatus.HighConfidenceCandidate)
                .Sum(item => item.AuditableTechnicianDays),
            candidates,
            timeProvider.GetUtcNow());
    }

    internal static HistoricalVehicleAssignmentCandidate Classify(
        Technician technician,
        IReadOnlyList<PowerfleetVehicleObservation> currentVehicles,
        IReadOnlyList<PhysicalVehicle> physicalVehicles,
        IReadOnlyList<NormalizedPilotTrip> julyTrips,
        IReadOnlyList<TechnicianVehicleAssignment> assignments,
        int auditableDays,
        DateOnly from,
        DateOnly through,
        TechnicianTrackingEligibility? trackingEligibility = null)
    {
        if (trackingEligibility?.TrackingStatus == TechnicianTrackingStatus.NoTrackAndTrace)
        {
            return new(technician.ExternalId, technician.Name, technician.Code,
                null, null, null, null, null, from, through,
                HistoricalVehicleCandidateStatus.NoTrackAndTrace,
                0, 0, auditableDays, [], [],
                [
                    "Geen Track & Trace — niet controleerbaar via voertuiglocatie",
                    $"{trackingEligibility.Source}: {trackingEligibility.Reason}",
                    "Geen vehicle assignment, DriverId of GPS-fit onderzocht.",
                ],
                $"{technician.ExternalId}:{from:yyyyMMdd}:{through:yyyyMMdd}");
        }
        var monthFrom = LocalStart(from);
        var monthTo = LocalStart(through.AddDays(1));
        var confirmed = assignments.Where(item =>
                item.TechnicianExternalId.Equals(technician.ExternalId,
                    StringComparison.OrdinalIgnoreCase) &&
                item.ValidFrom <= monthFrom &&
                (item.ValidTo is null || item.ValidTo >= monthTo))
            .ToArray();
        var exactCurrent = currentVehicles.Where(item =>
                NormalizeCode(item.Name) == NormalizeCode(technician.Code))
            .ToArray();
        var driverIds = julyTrips.Where(item =>
                !string.IsNullOrWhiteSpace(item.DriverId) &&
                ExactPersonName(item.DriverName, technician.Name))
            .Select(item => item.DriverId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var driverTrips = julyTrips.Where(item =>
                !string.IsNullOrWhiteSpace(item.DriverId) &&
                driverIds.Contains(item.DriverId, StringComparer.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.ObjectId))
            .ToArray();

        PowerfleetVehicleObservation? proposed = null;
        if (confirmed.Length == 1)
        {
            proposed = currentVehicles.FirstOrDefault(item => item.ObjectId.Equals(
                           confirmed[0].ObjectId, StringComparison.OrdinalIgnoreCase)) ??
                       ToObservation(physicalVehicles.FirstOrDefault(item => item.ObjectId.Equals(
                           confirmed[0].ObjectId, StringComparison.OrdinalIgnoreCase)));
        }
        else if (exactCurrent.Length == 1)
        {
            proposed = exactCurrent[0];
        }

        var proposedTrips = proposed is null
            ? []
            : julyTrips.Where(item => item.ObjectId?.Equals(
                    proposed.ObjectId, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
        var objectGroups = driverTrips.GroupBy(item => item.ObjectId!, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var alternatives = objectGroups.Select(group => Alternative(
                group.Key, group.ToArray(), currentVehicles, physicalVehicles))
            .OrderByDescending(item => item.JulyTripDays)
            .ThenBy(item => item.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var competing = proposed is null
            ? alternatives
            : alternatives.Where(item => !item.ObjectId.Equals(
                    proposed.ObjectId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var evidence = new List<string>();
        HistoricalVehicleCandidateStatus status;
        if (confirmed.Length == 1)
        {
            status = HistoricalVehicleCandidateStatus.AlreadyConfirmed;
            evidence.Add($"Bevestigde assignment dekt de volledige periode: {confirmed[0].ObjectId}.");
        }
        else if (confirmed.Length > 1)
        {
            status = HistoricalVehicleCandidateStatus.MultipleCandidates;
            evidence.Add($"{confirmed.Length} bevestigde assignments overlappen de volledige maand.");
        }
        else if (exactCurrent.Length > 1)
        {
            status = HistoricalVehicleCandidateStatus.MultipleCandidates;
            evidence.Add($"{exactCurrent.Length} actuele voertuigen dragen exact RESCODE {technician.Code}.");
        }
        else if (proposed is null)
        {
            status = alternatives.Length > 1
                ? HistoricalVehicleCandidateStatus.MultipleCandidates
                : HistoricalVehicleCandidateStatus.NoCandidate;
            evidence.Add("Geen uniek actueel voertuig met exacte PowerFleet.Name ↔ RESOURCE.RESCODE.");
        }
        else if (proposedTrips.Length == 0)
        {
            status = HistoricalVehicleCandidateStatus.NoCandidate;
            evidence.Add("Actuele exacte naamkoppeling bestaat, maar dit ObjectId heeft geen juliritten.");
            evidence.Add("De huidige naam alleen bewijst geen historische juli-assignment.");
        }
        else if (competing.Length > 0)
        {
            status = HistoricalVehicleCandidateStatus.TransferSuspected;
            evidence.Add($"{competing.Length} concurrerende ObjectId(s) in ondersteunende DriverId-context.");
            evidence.Add("Mogelijke transfer of voertuig-/teamcontext; menselijke periodebevestiging vereist.");
        }
        else
        {
            status = HistoricalVehicleCandidateStatus.HighConfidenceCandidate;
            evidence.Add("Actuele PowerFleet.Name is exact gelijk aan RESOURCE.RESCODE.");
            evidence.Add("Hetzelfde fysieke ObjectId heeft aantoonbare juliritten.");
            evidence.Add("Geen concurrerend ObjectId in de beschikbare ondersteunende DriverId-context.");
        }

        if (proposed is not null && proposedTrips.Length > 0)
        {
            evidence.Add($"{proposedTrips.Select(TripDate).Distinct().Count()} actieve julidagen, " +
                         $"{proposedTrips.Length} ritten op ObjectId {proposed.ObjectId}.");
        }
        if (driverIds.Length > 0)
            evidence.Add($"Ondersteunende DriverId-context: {string.Join(", ", driverIds)}; niet authoritative.");
        evidence.Add("Geen GPS/Plenion-best-fit gebruikt.");

        return new(technician.ExternalId, technician.Name, technician.Code,
            proposed?.ObjectId, proposed?.RegistrationPlate, proposed?.Name,
            proposed?.Make, proposed?.Model, from, through, status,
            proposedTrips.Select(TripDate).Distinct().Count(), proposedTrips.Length,
            auditableDays, driverIds, alternatives, evidence,
            $"{technician.ExternalId}:{from:yyyyMMdd}:{through:yyyyMMdd}");
    }

    private static HistoricalVehicleAlternative Alternative(
        string objectId,
        NormalizedPilotTrip[] trips,
        IReadOnlyList<PowerfleetVehicleObservation> current,
        IReadOnlyList<PhysicalVehicle> physical)
    {
        var vehicle = current.FirstOrDefault(item => item.ObjectId.Equals(
            objectId, StringComparison.OrdinalIgnoreCase));
        var stored = physical.FirstOrDefault(item => item.ObjectId.Equals(
            objectId, StringComparison.OrdinalIgnoreCase));
        return new(objectId, vehicle?.RegistrationPlate ?? stored?.RegistrationPlate,
            vehicle?.Name ?? stored?.Name ?? trips.Select(item => item.ObjectName).FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item)),
            trips.Select(TripDate).Distinct().Count(),
            trips.Min(item => item.StartDateTime), trips.Max(item => item.EndDateTime),
            trips.Select(item => item.DriverId).Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static PowerfleetVehicleObservation? ToObservation(PhysicalVehicle? vehicle) =>
        vehicle is null ? null : new(vehicle.ObjectId, vehicle.RegistrationPlate,
            vehicle.Name, vehicle.Make, vehicle.Model, vehicle.IsActive);

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool ExactPersonName(string? left, string right) =>
        string.Equals(NormalizePersonName(left), NormalizePersonName(right),
            StringComparison.Ordinal);

    private static string NormalizePersonName(string? value) => string.Join(' ',
        (value ?? string.Empty).Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static DateOnly TripDate(NormalizedPilotTrip trip) =>
        DateOnly.FromDateTime(trip.StartDateTime.DateTime);

    private static DateTimeOffset LocalStart(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.FromHours(2));

    private static IEnumerable<DateOnly> Dates(DateOnly from, DateOnly through)
    {
        for (var date = from; date <= through; date = date.AddDays(1)) yield return date;
    }

    private static DailyAbsenceWindow[] CalendarWindows(
        IEnumerable<PlenionCalendarAbsence> absences,
        DateOnly date) => absences.Select(item => new DailyAbsenceWindow(
            new DateTimeOffset(date.ToDateTime(item.StartTime), TimeSpan.FromHours(2)),
            new DateTimeOffset(date.ToDateTime(item.EndTime), TimeSpan.FromHours(2)),
            item.Kind,
            item.Subject)).ToArray();
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

internal sealed class TechnicianVehicleAssignmentService(
    IDbContextFactory<TimeControlDbContext> contextFactory)
{
    public async Task<VehicleAssignmentResolution> ResolveAsync(
        string technicianExternalId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var matches = await context.TechnicianVehicleAssignments
            .AsNoTracking()
            .Where(item => item.TechnicianExternalId == technicianExternalId)
            .ToArrayAsync(cancellationToken);
        var uncertainTransfer = matches
            .Where(item => item.PreviousObservedAt is not null &&
                           item.ValidFrom > item.PreviousObservedAt &&
                           item.EvidenceReference != null &&
                           item.EvidenceReference.Contains(
                               "SyncMomentIsNotConfirmedTransferTime=true"))
            .FirstOrDefault(item =>
                at > item.PreviousObservedAt!.Value && at < item.ValidFrom);
        if (uncertainTransfer is not null)
        {
            var windowAssignments = matches.Where(item =>
                    item.ObjectId == uncertainTransfer.ObjectId ||
                    item.ValidTo == uncertainTransfer.ValidFrom)
                .OrderBy(item => item.ValidFrom)
                .ToArray();
            return new(
                VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment,
                at,
                technicianExternalId,
                null,
                windowAssignments,
                $"Voertuigtransfer alleen waargenomen tussen " +
                $"{uncertainTransfer.PreviousObservedAt:O} en {uncertainTransfer.ValidFrom:O}; " +
                "geen voertuig gekozen binnen dit uncertainty window.");
        }
        matches = matches.Where(item => item.IsValidAt(at))
            .OrderBy(item => item.ValidFrom)
            .ToArray();
        return matches.Length switch
        {
            0 => new(VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment, at,
                technicianExternalId, null, matches,
                "Geen tijdsgeldige voertuigtoewijzing; DriverId en route-fit worden niet als fallback gebruikt."),
            1 => new(VehicleAssignmentResolutionStatus.Resolved, at, technicianExternalId,
                matches[0].ObjectId, matches, "Eén tijdsgeldige ObjectId-toewijzing."),
            _ => new(VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment, at,
                technicianExternalId, null, matches,
                $"{matches.Length} overlappende tijdsgeldige voertuigtoewijzingen."),
        };
    }
}

public sealed class TechnicianVehicleAssignmentBackfillService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IPlenionReader plenionReader,
    TimeProvider timeProvider)
{
    public async Task<TechnicianVehicleAssignment> RegisterAsync(
        VehicleAssignmentBackfillRequest request,
        CancellationToken cancellationToken)
    {
        var technicians = await plenionReader.GetTechniciansAsync(cancellationToken);
        return await RegisterAsync(request, technicians, cancellationToken);
    }

    internal async Task<TechnicianVehicleAssignment> RegisterAsync(
        VehicleAssignmentBackfillRequest request,
        IReadOnlyList<Technician> technicians,
        CancellationToken cancellationToken)
    {
        return AssertSingle(await RegisterManyAsync(
            [request], technicians, cancellationToken));
    }

    public async Task<IReadOnlyList<TechnicianVehicleAssignment>> RegisterManyAsync(
        IReadOnlyList<VehicleAssignmentBackfillRequest> requests,
        CancellationToken cancellationToken)
    {
        var technicians = await plenionReader.GetTechniciansAsync(cancellationToken);
        return await RegisterManyAsync(requests, technicians, cancellationToken);
    }

    internal async Task<IReadOnlyList<TechnicianVehicleAssignment>> RegisterManyAsync(
        IReadOnlyList<VehicleAssignmentBackfillRequest> requests,
        IReadOnlyList<Technician> technicians,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) throw new ArgumentException("Geen assignments geselecteerd.");
        foreach (var request in requests) ValidateRequest(request);
        var mapped = requests.Select(request =>
        {
            var matches = ExactCodeMatches(technicians, request.TechnicianCode);
            if (matches.Length == 0)
                throw new InvalidOperationException(
                    $"TechnicianCode {request.TechnicianCode} bestaat niet exact in Plenion RESOURCE.");
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"AmbiguousTechnicianCode: {request.TechnicianCode} is niet uniek.");
            return (Request: request, Technician: matches[0]);
        }).ToArray();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var objectIds = requests.Select(item => item.ObjectId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var vehicles = await context.PhysicalVehicles
            .Where(item => objectIds.Contains(item.ObjectId))
            .ToArrayAsync(cancellationToken);
        var vehicleById = vehicles.ToDictionary(item => item.ObjectId, StringComparer.OrdinalIgnoreCase);
        foreach (var objectId in objectIds)
        {
            if (!vehicleById.ContainsKey(objectId))
                throw new InvalidOperationException($"ObjectId {objectId} bestaat niet als PhysicalVehicle.");
        }

        var existing = await context.TechnicianVehicleAssignments.AsNoTracking()
            .ToArrayAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var pending = new List<TechnicianVehicleAssignment>();
        foreach (var item in mapped)
        {
            var request = item.Request;
            var objectId = request.ObjectId.Trim();
            var conflicts = existing.Concat(pending).Where(assignment =>
                    (assignment.TechnicianExternalId == item.Technician.ExternalId ||
                     assignment.ObjectId.Equals(objectId, StringComparison.OrdinalIgnoreCase)) &&
                    PeriodsOverlap(request.ValidFrom, request.ValidTo,
                        assignment.ValidFrom, assignment.ValidTo))
                .ToArray();
            if (conflicts.Length > 0)
                throw new InvalidOperationException(
                    $"AssignmentOverlap voor {request.TechnicianCode}/{objectId}: " +
                    $"{conflicts.Length} assignment(s); bulkactie volledig geannuleerd.");

            var historicalAdminConfirmation = request.Source.Equals(
                "HistoricalAdminConfirmation", StringComparison.OrdinalIgnoreCase);
            pending.Add(new TechnicianVehicleAssignment
            {
                TechnicianExternalId = item.Technician.ExternalId,
                TechnicianCode = NormalizeCode(item.Technician.Code),
                ObjectId = objectId,
                RegistrationPlateSnapshot = vehicleById[objectId].RegistrationPlate,
                ValidFrom = request.ValidFrom,
                ValidTo = request.ValidTo,
                Source = request.Source.Trim(),
                Confidence = historicalAdminConfirmation ? "Confirmed" : "ExplicitlyConfirmed",
                ObservedAt = now,
                EvidenceReference = request.EvidenceReference.Trim(),
                CreatedAt = now,
                ReviewedBy = request.Actor.Trim(),
                ReviewedAt = now,
            });
        }

        context.TechnicianVehicleAssignments.AddRange(pending);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var pair in pending.Zip(requests))
        {
            context.TechnicianVehicleAssignmentAudits.Add(Audit(
                pair.First.Id, "HistoricalBackfillCreated", pair.Second.Actor,
                pair.Second.Source, now, null, pair.First, pair.Second.EvidenceReference));
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending;
    }

    private static bool PeriodsOverlap(
        DateTimeOffset leftFrom,
        DateTimeOffset? leftTo,
        DateTimeOffset rightFrom,
        DateTimeOffset? rightTo) =>
        (leftTo is null || rightFrom < leftTo) &&
        (rightTo is null || leftFrom < rightTo);

    private static TechnicianVehicleAssignment AssertSingle(
        IReadOnlyList<TechnicianVehicleAssignment> assignments) => assignments.Single();

    private static void ValidateRequest(VehicleAssignmentBackfillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TechnicianCode) ||
            string.IsNullOrWhiteSpace(request.ObjectId) ||
            string.IsNullOrWhiteSpace(request.Source) ||
            string.IsNullOrWhiteSpace(request.EvidenceReference) ||
            string.IsNullOrWhiteSpace(request.Actor))
        {
            throw new ArgumentException("TechnicianCode, ObjectId, Source, evidence en actor zijn verplicht.");
        }
        if (request.ValidTo is not null && request.ValidTo <= request.ValidFrom)
        {
            throw new ArgumentException("ValidTo moet exclusief en later dan ValidFrom zijn.");
        }
    }

    internal static Technician[] ExactCodeMatches(
        IEnumerable<Technician> technicians,
        string code) => technicians.Where(item =>
            NormalizeCode(item.Code) == NormalizeCode(code)).ToArray();

    internal static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();

    internal static TechnicianVehicleAssignmentAudit Audit(
        int? assignmentId,
        string action,
        string actor,
        string source,
        DateTimeOffset changedAt,
        TechnicianVehicleAssignment? oldAssignment,
        TechnicianVehicleAssignment? newAssignment,
        string? evidence) => new()
        {
            AssignmentId = assignmentId,
            Action = action,
            Actor = actor,
            Source = source,
            ChangedAt = changedAt,
            OldAssignmentJson = oldAssignment is null ? null : JsonSerializer.Serialize(oldAssignment),
            NewAssignmentJson = newAssignment is null ? null : JsonSerializer.Serialize(newAssignment),
            EvidenceReference = evidence,
        };
}

internal sealed record VehicleAssignmentSyncResult(
    int Vehicles,
    int PhysicalVehiclesObserved,
    int ExactMapped,
    int Unmapped,
    int Ambiguous,
    int ResourcesWithoutPersonalVehicle,
    int AssignmentsOpened,
    int AssignmentsClosed,
    int AssignmentsObserved,
    int SkippedNoTrackAndTrace,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> UnmappedNames,
    IReadOnlyList<string> AmbiguousNames,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    double? DurationSeconds = null);

internal sealed class TechnicianVehicleAssignmentSyncService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IPlenionReader plenionReader,
    PowerfleetVehicleReader vehicleReader,
    TimeProvider timeProvider,
    VehicleAssignmentSyncHistoryService historyService)
{
    public async Task<VehicleAssignmentSyncResult> RunAsync(
        string actor,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var run = await historyService.StartAsync(startedAt, cancellationToken);
        try
        {
            var observations = await vehicleReader.ReadAsync(cancellationToken);
            var resources = await plenionReader.GetTechniciansAsync(cancellationToken);
            var applied = await ApplyAsync(
                observations, resources, startedAt, actor, cancellationToken);
            var finishedAt = timeProvider.GetUtcNow();
            var result = applied with
            {
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                DurationSeconds = Math.Max(0, (finishedAt - startedAt).TotalSeconds),
            };
            await historyService.CompleteAsync(run.Id, result, finishedAt, cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await historyService.FailAsync(run.Id, exception, CancellationToken.None);
            throw;
        }
    }

    internal async Task<VehicleAssignmentSyncResult> RunSnapshotAsync(
        IReadOnlyList<PowerfleetVehicleObservation> observations,
        IReadOnlyList<Technician> resources,
        DateTimeOffset observedAt,
        string actor,
        CancellationToken cancellationToken)
    {
        var run = await historyService.StartAsync(observedAt, cancellationToken);
        try
        {
            var applied = await ApplyAsync(
                observations, resources, observedAt, actor, cancellationToken);
            var finishedAt = timeProvider.GetUtcNow();
            var result = applied with
            {
                StartedAt = observedAt,
                FinishedAt = finishedAt,
                DurationSeconds = Math.Max(0, (finishedAt - observedAt).TotalSeconds),
            };
            await historyService.CompleteAsync(run.Id, result, finishedAt, cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await historyService.FailAsync(run.Id, exception, CancellationToken.None);
            throw;
        }
    }

    internal async Task<VehicleAssignmentSyncResult> ApplyAsync(
        IReadOnlyList<PowerfleetVehicleObservation> observations,
        IReadOnlyList<Technician> resources,
        DateTimeOffset observedAt,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is verplicht.");
        var activeResources = resources.Where(item =>
                item.Kind == 1 &&
                (item.EmploymentStart is null || item.EmploymentStart <= DateOnly.FromDateTime(observedAt.Date)) &&
                (item.EmploymentEnd is null || item.EmploymentEnd >= DateOnly.FromDateTime(observedAt.Date)))
            .ToArray();
        var byCode = activeResources
            .Where(item => !string.IsNullOrWhiteSpace(item.Code))
            .GroupBy(item => TechnicianVehicleAssignmentBackfillService.NormalizeCode(item.Code))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var mapped = observations.Select(observation =>
        {
            Technician[]? matches = null;
            if (!string.IsNullOrWhiteSpace(observation.Name))
                byCode.TryGetValue(
                    TechnicianVehicleAssignmentBackfillService.NormalizeCode(observation.Name),
                    out matches);
            return (Observation: observation, Matches: matches ?? []);
        }).ToArray();
        var duplicateTechnicians = mapped.Where(item => item.Matches.Length == 1)
            .GroupBy(item => item.Matches[0].ExternalId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Observation.ObjectId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var trackingEligibilities = await context.TechnicianTrackingEligibilities.AsNoTracking()
            .ToArrayAsync(cancellationToken);
        var noTrackResourceIds = activeResources.Where(resource =>
                trackingEligibilities.Any(item =>
                    item.TechnicianExternalId.Equals(resource.ExternalId, StringComparison.OrdinalIgnoreCase) &&
                    item.TrackingStatus == TechnicianTrackingStatus.NoTrackAndTrace &&
                    item.IsValidAt(observedAt)))
            .Select(item => item.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opened = 0;
        var closed = 0;
        var observed = 0;
        foreach (var observation in observations)
        {
            var objectId = observation.ObjectId.Trim();
            var physical = await context.PhysicalVehicles.SingleOrDefaultAsync(
                item => item.ObjectId == objectId, cancellationToken);
            if (physical is null)
            {
                physical = new PhysicalVehicle
                {
                    ObjectId = objectId,
                    FirstObservedAt = observedAt,
                };
                context.PhysicalVehicles.Add(physical);
            }
            physical.RegistrationPlate = observation.RegistrationPlate?.Trim();
            physical.Name = observation.Name.Trim();
            physical.Make = observation.Make?.Trim();
            physical.Model = observation.Model?.Trim();
            physical.LastObservedAt = observedAt;
            physical.IsActive = observation.IsActive;
            physical.Source = "PowerFleet Vehicles/get";
        }
        await context.SaveChangesAsync(cancellationToken);

        foreach (var item in mapped)
        {
            var observation = item.Observation;
            if (item.Matches.Length == 1 &&
                noTrackResourceIds.Contains(item.Matches[0].ExternalId))
                continue;
            var exact = item.Matches.Length == 1 &&
                        !duplicateTechnicians.Contains(item.Matches[0].ExternalId);
            if (!exact)
                continue;

            var technician = item.Matches[0];
            var openForObject = await context.TechnicianVehicleAssignments
                .Where(assignment => assignment.ObjectId == observation.ObjectId && assignment.ValidTo == null)
                .ToArrayAsync(cancellationToken);
            var openForTechnician = await context.TechnicianVehicleAssignments
                .Where(assignment => assignment.TechnicianExternalId == technician.ExternalId &&
                                     assignment.ValidTo == null)
                .ToArrayAsync(cancellationToken);
            var same = openForTechnician.SingleOrDefault(assignment =>
                assignment.ObjectId == observation.ObjectId);
            if (same is not null)
            {
                foreach (var conflict in openForTechnician.Concat(openForObject)
                             .Where(value => value.Id != same.Id)
                             .DistinctBy(value => value.Id))
                {
                    Close(context, conflict, observedAt, actor,
                        $"Exacte actuele observatie bevestigt {technician.Code}/{observation.ObjectId}; " +
                        $"conflicterende open assignment gesloten. Vorige observatie {conflict.ObservedAt:O}.",
                        ref closed);
                }
                var old = Snapshot(same);
                same.PreviousObservedAt = same.ObservedAt;
                same.ObservedAt = observedAt;
                same.RegistrationPlateSnapshot = observation.RegistrationPlate;
                context.TechnicianVehicleAssignmentAudits.Add(
                    TechnicianVehicleAssignmentBackfillService.Audit(
                        same.Id, "Observed", actor, "PowerFleet Vehicles/get", observedAt,
                        old, same, $"PreviousObservedAt={old.ObservedAt:O};ObservedAt={observedAt:O}"));
                observed++;
                continue;
            }

            var previousAssignments = openForTechnician.Concat(openForObject)
                .DistinctBy(value => value.Id)
                .ToArray();
            var previousObservedAt = previousAssignments.Length == 0
                ? (DateTimeOffset?)null
                : previousAssignments.Max(item => item.ObservedAt);
            foreach (var previous in previousAssignments)
            {
                Close(context, previous, observedAt, actor,
                    $"Transfervenster: vorige observatie {previous.ObservedAt:O}; nieuwe observatie {observedAt:O}.",
                    ref closed);
            }

            var assignment = new TechnicianVehicleAssignment
            {
                TechnicianExternalId = technician.ExternalId,
                TechnicianCode = TechnicianVehicleAssignmentBackfillService.NormalizeCode(technician.Code),
                ObjectId = observation.ObjectId,
                RegistrationPlateSnapshot = observation.RegistrationPlate,
                ValidFrom = observedAt,
                Source = "AutomaticCurrentMasterDataSync: PowerFleet Vehicles/get + exact Plenion RESOURCE.RESCODE",
                Confidence = "ExactCurrentObservation",
                ObservedAt = observedAt,
                PreviousObservedAt = previousObservedAt,
                EvidenceReference = $"Name={observation.Name};ObjectId={observation.ObjectId};" +
                                    $"PreviousObservedAt={previousObservedAt?.ToString("O") ?? "none"};" +
                                    $"ObservedAt={observedAt:O};SyncMomentIsNotConfirmedTransferTime=true",
                CreatedAt = observedAt,
            };
            context.TechnicianVehicleAssignments.Add(assignment);
            await context.SaveChangesAsync(cancellationToken);
            context.TechnicianVehicleAssignmentAudits.Add(
                TechnicianVehicleAssignmentBackfillService.Audit(
                    assignment.Id, "Opened", actor, assignment.Source, observedAt,
                    null, assignment, assignment.EvidenceReference));
            opened++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var exactMapped = mapped.Count(item => item.Matches.Length == 1 &&
            !duplicateTechnicians.Contains(item.Matches[0].ExternalId) &&
            !noTrackResourceIds.Contains(item.Matches[0].ExternalId));
        var ambiguousNames = mapped.Where(item => item.Matches.Length > 1 ||
                (item.Matches.Length == 1 &&
                 !noTrackResourceIds.Contains(item.Matches[0].ExternalId) &&
                 duplicateTechnicians.Contains(item.Matches[0].ExternalId)))
            .Select(item => item.Observation.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var unmappedNames = mapped.Where(item => item.Matches.Length == 0)
            .Select(item => string.IsNullOrWhiteSpace(item.Observation.Name)
                ? "<missing Name>"
                : item.Observation.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mappedResourceIds = mapped.Where(item => item.Matches.Length == 1 &&
                !duplicateTechnicians.Contains(item.Matches[0].ExternalId) &&
                !noTrackResourceIds.Contains(item.Matches[0].ExternalId))
            .Select(item => item.Matches[0].ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedNoTrackAndTrace = mapped.Where(item => item.Matches.Length == 1 &&
                noTrackResourceIds.Contains(item.Matches[0].ExternalId))
            .Select(item => item.Matches[0].ExternalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new(observations.Count, observations.Count, exactMapped,
            unmappedNames.Length, ambiguousNames.Length,
            activeResources.Count(item =>
                !noTrackResourceIds.Contains(item.ExternalId) &&
                !mappedResourceIds.Contains(item.ExternalId)),
            opened, closed, observed, skippedNoTrackAndTrace,
            observedAt, unmappedNames, ambiguousNames);
    }

    private static void Close(
        TimeControlDbContext context,
        TechnicianVehicleAssignment assignment,
        DateTimeOffset at,
        string actor,
        string evidence,
        ref int count)
    {
        var old = Snapshot(assignment);
        assignment.ValidTo = at;
        assignment.PreviousObservedAt = assignment.ObservedAt;
        assignment.ObservedAt = at;
        context.TechnicianVehicleAssignmentAudits.Add(
            TechnicianVehicleAssignmentBackfillService.Audit(
                assignment.Id, "Closed", actor, "PowerFleet Vehicles/get", at,
                old, assignment, evidence));
        count++;
    }

    private static TechnicianVehicleAssignment Snapshot(TechnicianVehicleAssignment value) => new()
    {
        Id = value.Id,
        TechnicianExternalId = value.TechnicianExternalId,
        TechnicianCode = value.TechnicianCode,
        ObjectId = value.ObjectId,
        RegistrationPlateSnapshot = value.RegistrationPlateSnapshot,
        ValidFrom = value.ValidFrom,
        ValidTo = value.ValidTo,
        Source = value.Source,
        Confidence = value.Confidence,
        ObservedAt = value.ObservedAt,
        PreviousObservedAt = value.PreviousObservedAt,
        EvidenceReference = value.EvidenceReference,
        CreatedAt = value.CreatedAt,
        ReviewedBy = value.ReviewedBy,
        ReviewedAt = value.ReviewedAt,
    };
}

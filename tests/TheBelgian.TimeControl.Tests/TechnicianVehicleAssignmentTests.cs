using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Tests;

public sealed class TechnicianVehicleAssignmentTests
{
    [Fact]
    public async Task Sync_UsesOnlyUniqueExactNormalizedRescode()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = Sync(database, At(2026, 8, 25));
        var resources = new[] { Technician("1", "rco", "Rajco") };

        var result = await service.ApplyAsync(
            [Vehicle("91921", " RCO ")], resources, At(2026, 8, 25), "test", default);

        Assert.Equal(1, result.ExactMapped);
        var assignment = Assert.Single(await database.Assignments());
        Assert.Equal("1", assignment.TechnicianExternalId);
        Assert.Equal("RCO", assignment.TechnicianCode);
        Assert.Equal("91921", assignment.ObjectId);
        Assert.Equal(At(2026, 8, 25), assignment.ValidFrom);
    }

    [Fact]
    public async Task Sync_UnknownNameAndDuplicateCode_DoNotCreateAssignments()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = Sync(database, At(2026, 8, 25));
        var resources = new[]
        {
            Technician("1", "DUP", "One"), Technician("2", "dup", "Two"),
        };

        var result = await service.ApplyAsync(
            [Vehicle("a", "UNKNOWN"), Vehicle("b", "DUP")],
            resources, At(2026, 8, 25), "test", default);

        Assert.Equal(1, result.Unmapped);
        Assert.Equal(1, result.Ambiguous);
        Assert.Empty(await database.Assignments());
    }

    [Fact]
    public async Task Resolver_ReturnsZeroOneAndOverlappingOutcomes_WithExclusiveValidTo()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddVehicleAndAssignments(
            [Assignment("1", "RCO", "a", At(2026, 7, 1), At(2026, 7, 15)),
             Assignment("1", "RCO", "b", At(2026, 7, 10), null)]);
        var resolver = new TechnicianVehicleAssignmentService(database);

        var none = await resolver.ResolveAsync("2", At(2026, 7, 10), default);
        var overlap = await resolver.ResolveAsync("1", At(2026, 7, 12), default);
        var one = await resolver.ResolveAsync("1", At(2026, 7, 15), default);

        Assert.Equal(VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment, none.Status);
        Assert.Equal(VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment, overlap.Status);
        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved, one.Status);
        Assert.Equal("b", one.ObjectId);
    }

    [Fact]
    public async Task Sync_TransferClosesOldAndOpensNewAtObservation_WithAuditTrail()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = Sync(database, At(2026, 8, 26));
        var resources = new[] { Technician("1", "RCO", "Rajco") };
        await service.ApplyAsync([Vehicle("old", "RCO")], resources,
            At(2026, 8, 25), "sync", default);

        var result = await service.ApplyAsync([Vehicle("new", "RCO")], resources,
            At(2026, 8, 26), "sync", default);

        Assert.Equal(1, result.AssignmentsClosed);
        Assert.Equal(1, result.AssignmentsOpened);
        var assignments = await database.Assignments();
        Assert.Equal(At(2026, 8, 26), assignments.Single(item => item.ObjectId == "old").ValidTo);
        Assert.Null(assignments.Single(item => item.ObjectId == "new").ValidTo);
        Assert.Equal(At(2026, 8, 25),
            assignments.Single(item => item.ObjectId == "new").PreviousObservedAt);
        Assert.Contains("SyncMomentIsNotConfirmedTransferTime=true",
            assignments.Single(item => item.ObjectId == "new").EvidenceReference);
        Assert.Contains(await database.Audits(), item => item.Action == "Closed" &&
            item.OldAssignmentJson != null && item.NewAssignmentJson != null);
    }

    [Fact]
    public async Task Resolver_DoesNotGuessVehicleInsideObservedTransferUncertaintyWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = Sync(database, At(2026, 8, 26));
        var resources = new[] { Technician("1", "PJA", "New Technician") };
        await service.ApplyAsync([Vehicle("vehicle-a", "PJA")], resources,
            At(2026, 8, 23), "sync", default);
        await service.ApplyAsync([Vehicle("vehicle-b", "PJA")], resources,
            At(2026, 8, 26), "sync", default);
        var resolver = new TechnicianVehicleAssignmentService(database);

        var lastKnownA = await resolver.ResolveAsync("1", At(2026, 8, 23), default);
        var uncertain = await resolver.ResolveAsync("1", At(2026, 8, 24), default);
        var firstKnownB = await resolver.ResolveAsync("1", At(2026, 8, 26), default);

        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved, lastKnownA.Status);
        Assert.Equal("vehicle-a", lastKnownA.ObjectId);
        Assert.Equal(VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment, uncertain.Status);
        Assert.Null(uncertain.ObjectId);
        Assert.Contains("uncertainty window", uncertain.Reason);
        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved, firstKnownB.Status);
        Assert.Equal("vehicle-b", firstKnownB.ObjectId);
    }

    [Fact]
    public async Task Sync_NoTrackAndTrace_ObservesPhysicalVehicleButNeverChangesAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddVehicleAndAssignments(
            [Assignment("1", "JDO", "existing", At(2026, 8, 1), null)]);
        await database.AddTrackingEligibility(new TechnicianTrackingEligibility
        {
            TechnicianExternalId = "1", TechnicianCode = "JDO",
            TrackingStatus = TechnicianTrackingStatus.NoTrackAndTrace,
            Reason = "Geen persoonlijk Track & Trace voertuig", Source = "BusinessConfirmation",
            ValidFrom = At(2026, 1, 1), CreatedAt = At(2026, 1, 1), CreatedBy = "reviewer",
        });

        var result = await Sync(database, At(2026, 8, 25)).ApplyAsync(
            [Vehicle("vehicle", "JDO")], [Technician("1", "JDO", "Jan Dours")],
            At(2026, 8, 25), "sync", default);

        Assert.Equal(1, result.SkippedNoTrackAndTrace);
        var existing = Assert.Single(await database.Assignments());
        Assert.Equal("existing", existing.ObjectId);
        Assert.Null(existing.ValidTo);
        Assert.Contains(await database.PhysicalVehicles(), item =>
            item.ObjectId == "vehicle" && item.Name == "JDO");
    }

    [Fact]
    public async Task Sync_TwoVehiclesWithSameName_IsAmbiguousAndPreservesExistingAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddVehicleAndAssignments(
            [Assignment("1", "PJA", "old", At(2026, 8, 1), null)]);

        var result = await Sync(database, At(2026, 8, 25)).ApplyAsync(
            [Vehicle("old", "PJA"), Vehicle("new", "PJA")],
            [Technician("1", "PJA", "New Technician")],
            At(2026, 8, 25), "sync", default);

        Assert.Equal(1, result.Ambiguous);
        var existing = Assert.Single(await database.Assignments());
        Assert.Null(existing.ValidTo);
        Assert.Equal("old", existing.ObjectId);
    }

    [Fact]
    public async Task Sync_MissingVehicleName_ObservesPhysicalVehicleWithoutAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();
        var parsed = PowerfleetVehicleReader.Parse("""{"vehicles":[{"objectId":"12345"}]}""");

        var result = await Sync(database, At(2026, 8, 25)).ApplyAsync(
            parsed, [Technician("1", "PJA", "New Technician")],
            At(2026, 8, 25), "sync", default);

        Assert.Equal(1, result.Unmapped);
        Assert.Empty(await database.Assignments());
        Assert.Equal(string.Empty, Assert.Single(await database.PhysicalVehicles()).Name);
    }

    [Fact]
    public async Task Sync_MissingVehicleForOneSnapshot_DoesNotCloseExistingAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddVehicleAndAssignments(
            [Assignment("1", "PJA", "old", At(2026, 8, 1), null)]);

        await Sync(database, At(2026, 8, 25)).ApplyAsync(
            [], [Technician("1", "PJA", "New Technician")],
            At(2026, 8, 25), "sync", default);

        Assert.Null(Assert.Single(await database.Assignments()).ValidTo);
    }

    [Fact]
    public async Task Sync_FuzzyNameNeverCreatesAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();

        var result = await Sync(database, At(2026, 8, 25)).ApplyAsync(
            [Vehicle("12345", "PJA Technician")],
            [Technician("1", "PJA", "PJA Technician")],
            At(2026, 8, 25), "sync", default);

        Assert.Equal(1, result.Unmapped);
        Assert.Empty(await database.Assignments());
    }

    [Fact]
    public async Task Sync_TransferFailure_RollsBackClosedAssignmentAndNewAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddVehicleAndAssignments(
            [Assignment("1", "PJA", "old", At(2026, 8, 1), null)]);
        await database.FailAssignmentInsertForNewObject();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Sync(database, At(2026, 8, 25)).ApplyAsync(
                [Vehicle("new", "PJA")], [Technician("1", "PJA", "New Technician")],
                At(2026, 8, 25), "sync", default));

        var existing = Assert.Single(await database.Assignments());
        Assert.Equal("old", existing.ObjectId);
        Assert.Null(existing.ValidTo);
    }

    [Fact]
    public async Task SyncHistory_WritesSucceededRunAndReturnsLastSuccessfulTimestamp()
    {
        await using var database = await TestDatabase.CreateAsync();
        var at = At(2026, 8, 25);
        var service = Sync(database, at);

        var result = await service.RunSnapshotAsync(
            [Vehicle("12345", "PJA")], [Technician("1", "PJA", "New Technician")],
            at, "sync", default);
        var history = new VehicleAssignmentSyncHistoryService(
            database, new FixedTimeProvider(at));

        var run = Assert.Single(await database.SyncRuns());
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, run.PhysicalVehiclesObserved);
        Assert.Equal(1, run.AssignmentsOpened);
        Assert.Equal(result.FinishedAt,
            await history.LastSuccessfulVehicleAssignmentSyncAtAsync(default));
    }

    [Fact]
    public void SyncExecutionGuard_SecondConcurrentInstanceIsSkipped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"sync-{Guid.NewGuid():N}.db");
        using var first = VehicleAssignmentSyncExecutionGuard.TryAcquire(databasePath);
        using var second = VehicleAssignmentSyncExecutionGuard.TryAcquire(databasePath);

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
    }

    [Fact]
    public async Task BackfillRejectsOverlapAndNeverSilentlyOverwrites()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddVehicleAndAssignments(
            [Assignment("1", "RCO", "91921", At(2026, 8, 25), null)]);
        var service = new TechnicianVehicleAssignmentBackfillService(
            database, new FakePlenionReader([]), new FixedTimeProvider(At(2026, 8, 25)));
        var request = new VehicleAssignmentBackfillRequest(
            "RCO", "91921", At(2026, 7, 1), null, "admin", "confirmed", "tester");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterAsync(request, [Technician("1", "RCO", "Rajco")], default));

        Assert.StartsWith("AssignmentOverlap", error.Message);
        Assert.Single(await database.Assignments());
    }

    [Fact]
    public void ObjectAssignmentFiltersByObjectId_AndIgnoresDriverIdOnOtherObject()
    {
        var date = new DateOnly(2026, 7, 23);
        var resolution = new VehicleAssignmentResolution(
            VehicleAssignmentResolutionStatus.Resolved, At(2026, 7, 23), "1", "91921", [], "test");
        var trips = new[]
        {
            Trip("tesla-in", date, 8, 49, 91921, "15727"),
            Trip("tesla-out", date, 16, 49, 91921, "15727"),
            Trip("jav-in", date, 9, 3, 72679, "15727"),
            Trip("jav-out", date, 15, 44, 72679, "15727"),
        };

        var selected = DailyHoursAuditService.TripsForAssignment(trips, resolution);

        Assert.Equal(2, selected.Length);
        Assert.All(selected, item => Assert.Equal("91921", item.ObjectId));
        Assert.DoesNotContain(selected, item => item.ObjectId == "72679");
    }

    [Fact]
    public void ObjectIdWinsOverMatchingRegistrationPlate_AndPlateIsOnlyMissingIdFallback()
    {
        var date = new DateOnly(2026, 7, 23);
        var assignment = Assignment("1", "RCO", "91921", At(2026, 7, 1), null);
        assignment.RegistrationPlateSnapshot = "1-VTW-247";
        var resolution = new VehicleAssignmentResolution(
            VehicleAssignmentResolutionStatus.Resolved, At(2026, 7, 23), "1", "91921",
            [assignment], "test");
        var correct = Trip("correct", date, 8, 0, 91921, "driver");
        var wrongObject = Trip("wrong", date, 9, 0, 72679, "driver") with
        {
            VehiclePlate = "1-VTW-247",
        };
        var plateFallback = Trip("fallback", date, 10, 0, 91921, "driver") with
        {
            ObjectId = null,
            VehiclePlate = "1-VTW-247",
        };

        var selected = DailyHoursAuditService.TripsForAssignment(
            [correct, wrongObject, plateFallback], resolution);

        Assert.Equal(["correct", "fallback"], selected.Select(item => item.ExternalId));
    }

    [Fact]
    public void Rajco23July_AssignmentKeepsTeslaSiteDepartureAndRemovesFictive65Minutes()
    {
        var date = new DateOnly(2026, 7, 23);
        var resolution = new VehicleAssignmentResolution(
            VehicleAssignmentResolutionStatus.Resolved, At(2026, 7, 23), "1", "91921", [], "test");
        var trips = new[]
        {
            Trip("site-in", date, 8, 49, 91921, "15727", end: "Antwerpsesteenweg 136"),
            Trip("site-out", date, 16, 49, 91921, "15727", start: "Antwerpsesteenweg 136", departure: true),
            Trip("wrong-in", date, 9, 3, 72679, "15727", end: "Limaugestraat"),
            Trip("wrong-out", date, 15, 44, 72679, "15727", start: "Limaugestraat", departure: true),
        };

        var selected = DailyHoursAuditService.TripsForAssignment(trips, resolution);
        var stop = Assert.Single(PilotLocationMatcher.ReconstructStops(selected, []));

        Assert.Equal(new DateTimeOffset(2026, 7, 23, 8, 49, 0, TimeSpan.FromHours(2)), stop.Arrival);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 16, 49, 0, TimeSpan.FromHours(2)), stop.Departure);
        Assert.Equal("91921", stop.ObjectId);
    }

    [Fact]
    public async Task CurrentObservationNeverCreatesRetroactiveHistoricalAssignment()
    {
        await using var database = await TestDatabase.CreateAsync();
        var observedAt = At(2026, 8, 25);
        await Sync(database, observedAt).ApplyAsync(
            [Vehicle("91921", "RCO")], [Technician("1", "RCO", "Rajco")],
            observedAt, "sync", default);
        var resolver = new TechnicianVehicleAssignmentService(database);

        var july = await resolver.ResolveAsync("1", At(2026, 7, 23), default);

        Assert.Equal(VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment, july.Status);
    }

    [Fact]
    public void HistoricalCandidate_ExactRescodeStableObject_IsHighConfidence()
    {
        var technician = Technician("1", "YVE", "Yarne Vereecken");
        var trips = new[]
        {
            CandidateTrip("a", "100", "42", "Yarne Vereecken", 2),
            CandidateTrip("b", "100", "42", "Yarne Vereecken", 13),
        };

        var candidate = HistoricalVehicleAssignmentCandidateService.Classify(
            technician, [new("100", "2-HGW-338", "YVE", null, null)], [], trips, [],
            18, new(2026, 7, 1), new(2026, 7, 31));

        Assert.Equal(HistoricalVehicleCandidateStatus.HighConfidenceCandidate, candidate.Status);
        Assert.Equal("100", candidate.ProposedObjectId);
        Assert.Contains(candidate.Evidence, item => item.Contains("Geen GPS/Plenion-best-fit", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoricalCandidate_CurrentNameWithCompetingDriverObject_IsTransferSuspected()
    {
        var technician = Technician("1", "RCO", "Rajco Cools");
        var trips = new[]
        {
            CandidateTrip("tesla", "91921", "15727", "Rajco Cools", 23),
            CandidateTrip("other", "72679", "15727", "Rajco Cools", 23),
        };

        var candidate = HistoricalVehicleAssignmentCandidateService.Classify(
            technician, [new("91921", "1-VTW-247", "RCO", "Tesla", "Model 3")], [],
            trips, [], 11, new(2026, 7, 1), new(2026, 7, 31));

        Assert.Equal(HistoricalVehicleCandidateStatus.TransferSuspected, candidate.Status);
        Assert.Contains(candidate.Alternatives, item => item.ObjectId == "72679");
    }

    [Fact]
    public void HistoricalCandidate_DuplicateCurrentCode_IsMultipleCandidates()
    {
        var candidate = HistoricalVehicleAssignmentCandidateService.Classify(
            Technician("1", "DUP", "Technician"),
            [new("a", null, "DUP", null, null), new("b", null, "dup", null, null)],
            [], [CandidateTrip("a", "a", "1", "Technician", 2)], [], 5,
            new(2026, 7, 1), new(2026, 7, 31));

        Assert.Equal(HistoricalVehicleCandidateStatus.MultipleCandidates, candidate.Status);
    }

    [Fact]
    public void HistoricalCandidate_DoesNotChooseUnrelatedGpsBestFit()
    {
        var exact = CandidateTrip("identity", "assigned", null, null, 10) with
        {
            StartAddress = "far away",
            EndAddress = "far away",
        };
        var gpsFit = CandidateTrip("gps-fit", "other", "99", "Other Person", 10) with
        {
            StartAddress = "customer site",
            EndAddress = "customer site",
        };

        var candidate = HistoricalVehicleAssignmentCandidateService.Classify(
            Technician("1", "ABC", "Technician"),
            [new("assigned", "1-ABC-001", "ABC", null, null)], [],
            [exact, gpsFit], [], 7, new(2026, 7, 1), new(2026, 7, 31));

        Assert.Equal(HistoricalVehicleCandidateStatus.HighConfidenceCandidate, candidate.Status);
        Assert.Equal("assigned", candidate.ProposedObjectId);
    }

    [Fact]
    public void HistoricalCandidate_NoTrackAndTrace_SkipsAllVehicleEvidence()
    {
        var eligibility = new TechnicianTrackingEligibility
        {
            TechnicianExternalId = "1", TechnicianCode = "JDO",
            TrackingStatus = TechnicianTrackingStatus.NoTrackAndTrace,
            Reason = "Geen persoonlijk Track & Trace voertuig",
            Source = "BusinessConfirmation",
            ValidFrom = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            CreatedAt = At(2026, 8, 25), CreatedBy = "Benjamin Tonoli",
        };

        var candidate = HistoricalVehicleAssignmentCandidateService.Classify(
            Technician("1", "JDO", "Jan Dours"),
            [new("vehicle", "1-ABC-001", "JDO", null, null)], [],
            [CandidateTrip("trip", "vehicle", "1", "Jan Dours", 2)], [], 12,
            new(2026, 7, 1), new(2026, 7, 31), eligibility);

        Assert.Equal(HistoricalVehicleCandidateStatus.NoTrackAndTrace, candidate.Status);
        Assert.Null(candidate.ProposedObjectId);
        Assert.Equal(0, candidate.JulyTrips);
        Assert.Contains(candidate.Evidence, item => item.StartsWith("Geen Track & Trace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTrackAndTraceRegistration_IsPersistentAndResolvedByValidity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new TechnicianTrackingEligibilityService(
            database,
            new FakePlenionReader([Technician("1", "JDO", "Jan Dours")]),
            new FixedTimeProvider(At(2026, 8, 25)));
        var validFrom = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(2));

        var created = await service.RegisterNoTrackAndTraceAsync(
            ["JDO"], validFrom, "Geen persoonlijk Track & Trace voertuig",
            "BusinessConfirmation", "Benjamin Tonoli", default);
        var resolved = await service.ResolveAsync("1", validFrom.AddDays(10), default);

        Assert.Single(created);
        Assert.NotNull(resolved);
        Assert.Equal(TechnicianTrackingStatus.NoTrackAndTrace, resolved.TrackingStatus);
        Assert.Equal("Benjamin Tonoli", resolved.CreatedBy);
        Assert.Equal("BusinessConfirmation", resolved.Source);
    }

    [Fact]
    public async Task HistoricalAdminBulkConfirmation_IsAtomicAuditedAndTimeValid()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddPhysicalVehicles("a", "b");
        var technicians = new[]
        {
            Technician("1", "AAA", "One"), Technician("2", "BBB", "Two"),
        };
        var service = new TechnicianVehicleAssignmentBackfillService(
            database, new FakePlenionReader(technicians), new FixedTimeProvider(At(2026, 8, 25)));
        var requests = new[]
        {
            Confirmation("AAA", "a", "reviewer"), Confirmation("BBB", "b", "reviewer"),
        };

        var created = await service.RegisterManyAsync(requests, technicians, default);
        var resolver = new TechnicianVehicleAssignmentService(database);

        Assert.Equal(2, created.Count);
        Assert.All(created, item =>
        {
            Assert.Equal("HistoricalAdminConfirmation", item.Source);
            Assert.Equal("Confirmed", item.Confidence);
            Assert.Equal("reviewer", item.ReviewedBy);
            Assert.Equal(At(2026, 8, 25), item.ReviewedAt);
        });
        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved,
            (await resolver.ResolveAsync("1", new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.FromHours(2)), default)).Status);
        Assert.Equal(2, (await database.Audits()).Count(item => item.Action == "HistoricalBackfillCreated"));
    }

    private static VehicleAssignmentBackfillRequest Confirmation(
        string code, string objectId, string reviewer) => new(
        code, objectId,
        new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(2)),
        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(2)),
        "HistoricalAdminConfirmation", "explicit evidence", reviewer);

    private static NormalizedPilotTrip CandidateTrip(
        string id, string objectId, string? driverId, string? driverName, int day)
    {
        var start = new DateTimeOffset(2026, 7, day, 8, 0, 0, TimeSpan.FromHours(2));
        return new(id, start, start.AddMinutes(30), 30, null, 10,
            driverId, driverName, objectId, null, null,
            "from", "from", null, null, "to", "to", null, null,
            51m, 4m, 51m, 4m, "test");
    }

    private static TechnicianVehicleAssignmentSyncService Sync(
        TestDatabase database, DateTimeOffset now) => new(
        database,
        new FakePlenionReader([]),
        new PowerfleetVehicleReader(new HttpClient(), Options.Create(new PowerfleetOptions())),
        new FixedTimeProvider(now),
        new VehicleAssignmentSyncHistoryService(database, new FixedTimeProvider(now)));

    private static Technician Technician(string id, string code, string name) => new()
    {
        ExternalId = id, Code = code, Name = name, Kind = 1,
    };

    private static PowerfleetVehicleObservation Vehicle(string id, string name) =>
        new(id, id == "91921" ? "1-VTW-247" : null, name, "Make", "Model");

    private static TechnicianVehicleAssignment Assignment(
        string technician, string code, string objectId,
        DateTimeOffset from, DateTimeOffset? to) => new()
    {
        TechnicianExternalId = technician, TechnicianCode = code, ObjectId = objectId,
        ValidFrom = from, ValidTo = to, Source = "test", Confidence = "confirmed",
        ObservedAt = from, CreatedAt = from,
    };

    private static NormalizedPilotTrip Trip(
        string id, DateOnly date, int hour, int minute, int objectId, string driverId,
        string start = "from", string end = "to", bool departure = false)
    {
        var time = new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, 0,
            TimeSpan.FromHours(2));
        var tripStart = departure ? time : time.AddMinutes(-20);
        var tripEnd = departure ? time.AddMinutes(20) : time;
        return new(id, tripStart, tripEnd, 20, null, 5, driverId, "Rajco Cools",
            objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), objectId == 91921 ? "RCO" : "JAV", null,
            start, start, null, null, end, end, null, null,
            51m, 4m, 51m, 4m, "test");
    }

    private static DateTimeOffset At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakePlenionReader(IReadOnlyList<Technician> technicians) : IPlenionReader
    {
        public Task<IReadOnlyList<Technician>> GetTechniciansAsync(CancellationToken _) =>
            Task.FromResult(technicians);
        public Task<IReadOnlyList<PlenionPerformance>> GetPerformancesAsync(DateOnly _, DateOnly __, CancellationToken ___) =>
            Task.FromResult<IReadOnlyList<PlenionPerformance>>([]);
        public Task<IReadOnlyList<CustomerLocation>> GetCustomerLocationsAsync(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<CustomerLocation>>([]);
        public Task<IReadOnlyList<PlenionWorkOrder>> GetWorkOrdersAsync(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<PlenionWorkOrder>>([]);
        public Task<IReadOnlyList<PlenionProject>> GetProjectsAsync(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<PlenionProject>>([]);
    }

    private sealed class TestDatabase : IDbContextFactory<TimeControlDbContext>, IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<TimeControlDbContext> _options;

        private TestDatabase(SqliteConnection connection)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<TimeControlDbContext>()
                .UseSqlite(connection).Options;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var database = new TestDatabase(connection);
            await using var context = database.CreateDbContext();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public TimeControlDbContext CreateDbContext() => new(_options);
        public ValueTask<TimeControlDbContext> CreateDbContextAsync(CancellationToken _ = default) =>
            ValueTask.FromResult(CreateDbContext());

        public async Task AddVehicleAndAssignments(IEnumerable<TechnicianVehicleAssignment> values)
        {
            await using var context = CreateDbContext();
            var assignments = values.ToArray();
            foreach (var objectId in assignments.Select(item => item.ObjectId).Distinct())
            {
                context.PhysicalVehicles.Add(new PhysicalVehicle
                {
                    ObjectId = objectId, Name = objectId, Source = "test", IsActive = true,
                    FirstObservedAt = At(2026, 1, 1), LastObservedAt = At(2026, 1, 1),
                });
            }
            context.TechnicianVehicleAssignments.AddRange(assignments);
            await context.SaveChangesAsync();
        }

        public async Task AddPhysicalVehicles(params string[] objectIds)
        {
            await using var context = CreateDbContext();
            context.PhysicalVehicles.AddRange(objectIds.Select(objectId => new PhysicalVehicle
            {
                ObjectId = objectId,
                Name = objectId,
                Source = "test",
                IsActive = true,
                FirstObservedAt = At(2026, 1, 1),
                LastObservedAt = At(2026, 1, 1),
            }));
            await context.SaveChangesAsync();
        }

        public async Task AddTrackingEligibility(TechnicianTrackingEligibility value)
        {
            await using var context = CreateDbContext();
            context.TechnicianTrackingEligibilities.Add(value);
            await context.SaveChangesAsync();
        }

        public async Task FailAssignmentInsertForNewObject()
        {
            await using var context = CreateDbContext();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_assignment_insert
                BEFORE INSERT ON TechnicianVehicleAssignments
                WHEN NEW.ObjectId = 'new'
                BEGIN
                    SELECT RAISE(ABORT, 'forced transfer failure');
                END;
                """);
        }

        public async Task<PhysicalVehicle[]> PhysicalVehicles()
        {
            await using var context = CreateDbContext();
            return await context.PhysicalVehicles.AsNoTracking().ToArrayAsync();
        }

        public async Task<VehicleAssignmentSyncRun[]> SyncRuns()
        {
            await using var context = CreateDbContext();
            return await context.VehicleAssignmentSyncRuns.AsNoTracking().ToArrayAsync();
        }

        public async Task<TechnicianVehicleAssignment[]> Assignments()
        {
            await using var context = CreateDbContext();
            return await context.TechnicianVehicleAssignments.AsNoTracking().ToArrayAsync();
        }

        public async Task<TechnicianVehicleAssignmentAudit[]> Audits()
        {
            await using var context = CreateDbContext();
            return await context.TechnicianVehicleAssignmentAudits.AsNoTracking().ToArrayAsync();
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}

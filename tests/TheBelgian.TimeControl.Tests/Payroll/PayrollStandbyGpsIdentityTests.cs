using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollStandbyGpsIdentityTests
{
    private static readonly DateOnly Day = new(2026, 8, 15);

    [Fact]
    public void CanonicalObjectId_Resolves()
    {
        var assignments = new[]
        {
            Assignment("10", "OBJ-1", "1-ABC-123", Day.AddDays(-10), null),
        };
        var at = new DateTimeOffset(Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var resolution = TechnicianVehicleAssignmentService.ResolveFromSnapshot(assignments, "10", at);
        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("OBJ-1", resolution.ObjectId);
    }

    [Fact]
    public void NormalizedPlateFallback_ResolvesTripWithoutObjectId()
    {
        var resolution = new VehicleAssignmentResolution(
            VehicleAssignmentResolutionStatus.Resolved,
            new DateTimeOffset(Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero),
            "10",
            "OBJ-1",
            [Assignment("10", "OBJ-1", "1-ABC-123", Day.AddDays(-10), null)],
            "test");
        var trips = new[]
        {
            Trip(objectId: null, plate: "1 ABC 123", driverName: "Someone", km: 5m),
        };
        var matched = DailyHoursAuditService.TripsForAssignment(trips, resolution);
        Assert.Single(matched);
        Assert.Equal("1 ABC 123", matched[0].VehiclePlate);
    }

    [Theory]
    [InlineData("1ABC123", "1-ABC-123")]
    [InlineData("1-ABC-123", "1 ABC 123")]
    [InlineData("1 ABC 123", "1ABC123")]
    public void PlateFormatting_NormalizationIsConsistent(string left, string right)
    {
        Assert.Equal(
            PowerfleetPersonVehicleEvidence.NormalizePlate(left),
            PowerfleetPersonVehicleEvidence.NormalizePlate(right));
    }

    [Fact]
    public void HistoricalVehicleEvidence_DaySpecificDriverName()
    {
        var trips = new[]
        {
            Trip("OBJ-HIST", "1-XQA-898", "Aaron Spitaels", 12m),
            Trip("OBJ-OTHER", "9-ZZZ-999", "Other Person", 3m),
        };
        var evidence = PowerfleetPersonVehicleEvidence.ResolveFromDriverNameTrips("Aaron Spitaels", trips);
        Assert.True(evidence.HasUniqueObjectId);
        Assert.Equal("OBJ-HIST", evidence.ObjectId);
        Assert.Contains("DaySpecificDriverEvidence", evidence.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void VehicleChangedOverTime_UsesDateSpecificCanonical()
    {
        var assignments = new[]
        {
            Assignment("10", "OBJ-OLD", "1-OLD-001", new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1)),
            Assignment("10", "OBJ-NEW", "1-NEW-002", new DateOnly(2026, 8, 27), null),
        };
        var midAugust = new DateTimeOffset(new DateOnly(2026, 8, 15).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var lateAugust = new DateTimeOffset(new DateOnly(2026, 8, 29).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

        Assert.Equal(
            VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment,
            TechnicianVehicleAssignmentService.ResolveFromSnapshot(assignments, "10", midAugust).Status);
        var late = TechnicianVehicleAssignmentService.ResolveFromSnapshot(assignments, "10", lateAugust);
        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved, late.Status);
        Assert.Equal("OBJ-NEW", late.ObjectId);
    }

    [Fact]
    public void MultipleVehicles_Ambiguous()
    {
        var trips = new[]
        {
            Trip("OBJ-A", "1-AAA-111", "Jarno Vergauwen", 8m),
            Trip("OBJ-B", "1-BBB-222", "Jarno Vergauwen", 5m),
        };
        var evidence = PowerfleetPersonVehicleEvidence.ResolveFromDriverNameTrips("Jarno Vergauwen", trips);
        Assert.True(evidence.IsAmbiguous);
        Assert.False(evidence.HasUniqueObjectId);
        Assert.Contains("MULTIPLE_VEHICLES", evidence.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoVehicle_NotFakeMapping()
    {
        var evidence = PowerfleetPersonVehicleEvidence.ResolveFromDriverNameTrips(
            "Nobody",
            [Trip("OBJ-A", "1-AAA-111", "Someone Else", 8m)]);
        Assert.False(evidence.HasUniqueObjectId);
        Assert.False(evidence.IsAmbiguous);
        Assert.Null(evidence.ObjectId);
        Assert.Contains("NO_POWERFLEET_DATA", evidence.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DriverId_DoesNotSilentlyBecomePrimary()
    {
        // Canonical resolve ignores DriverId completely; only assignment ObjectId matters.
        var assignments = Array.Empty<TechnicianVehicleAssignment>();
        var at = new DateTimeOffset(Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var resolution = TechnicianVehicleAssignmentService.ResolveFromSnapshot(assignments, "10", at);
        Assert.Equal(VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment, resolution.Status);
        Assert.Contains("DriverId", resolution.Reason, StringComparison.Ordinal);
        Assert.Null(resolution.ObjectId);
    }

    [Fact]
    public void RajcoCanonicalCase_PreservedWhenAssignmentValid()
    {
        var assignments = new[]
        {
            Assignment("130", "91921", "1-VTW-247", new DateOnly(2026, 8, 27), null),
        };
        var at = new DateTimeOffset(new DateOnly(2026, 8, 29).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var resolution = TechnicianVehicleAssignmentService.ResolveFromSnapshot(assignments, "130", at);
        Assert.Equal(VehicleAssignmentResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("91921", resolution.ObjectId);

        var trips = new[]
        {
            Trip("91921", "1-VTW-247", "Rajco Cools", 20m, startHour: 20, endHour: 22),
        };
        var matched = DailyHoursAuditService.TripsForAssignment(trips, resolution);
        Assert.Single(matched);
        Assert.Equal("91921", matched[0].ObjectId);
    }

    private static TechnicianVehicleAssignment Assignment(
        string resourceId,
        string objectId,
        string plate,
        DateOnly from,
        DateOnly? to) =>
        new()
        {
            TechnicianExternalId = resourceId,
            TechnicianCode = "X",
            ObjectId = objectId,
            RegistrationPlateSnapshot = plate,
            ValidFrom = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(2)),
            ValidTo = to is null
                ? null
                : new DateTimeOffset(to.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(2)),
            Source = "test",
            Confidence = "Confirmed",
            ObservedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static NormalizedPilotTrip Trip(
        string? objectId,
        string? plate,
        string? driverName,
        decimal km,
        int startHour = 8,
        int endHour = 9) =>
        new(
            ExternalId: Guid.NewGuid().ToString("N"),
            StartDateTime: new DateTimeOffset(Day.ToDateTime(new TimeOnly(startHour, 0)), TimeSpan.Zero),
            EndDateTime: new DateTimeOffset(Day.ToDateTime(new TimeOnly(endHour, 0)), TimeSpan.Zero),
            DrivingMinutes: Math.Max(1, (endHour - startHour) * 60),
            StoppedAfterMinutes: null,
            DistanceKilometres: km,
            DriverId: "D1",
            DriverName: driverName,
            ObjectId: objectId,
            ObjectName: null,
            VehiclePlate: plate,
            StartLocation: null,
            StartAddress: null,
            StartArea: null,
            StartAreaGroup: null,
            EndLocation: null,
            EndAddress: null,
            EndArea: null,
            EndAreaGroup: null,
            StartLatitude: null,
            StartLongitude: null,
            EndLatitude: null,
            EndLongitude: null,
            Normalization: "test");
}

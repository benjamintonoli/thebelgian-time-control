namespace TheBelgian.TimeControl.Core.Models;

public sealed class PhysicalVehicle
{
    public int Id { get; set; }
    public string ObjectId { get; set; } = string.Empty;
    public string? RegistrationPlate { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Make { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsActive { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class TechnicianVehicleAssignment
{
    public int Id { get; set; }
    public string TechnicianExternalId { get; set; } = string.Empty;
    public string TechnicianCode { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string? RegistrationPlateSnapshot { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset? PreviousObservedAt { get; set; }
    public string? EvidenceReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public bool IsValidAt(DateTimeOffset instant) =>
        ValidFrom <= instant && (ValidTo is null || instant < ValidTo);
}

public sealed class TechnicianVehicleAssignmentAudit
{
    public int Id { get; set; }
    public int? AssignmentId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
    public string? OldAssignmentJson { get; set; }
    public string? NewAssignmentJson { get; set; }
    public string? EvidenceReference { get; set; }
}

public enum VehicleAssignmentResolutionStatus
{
    Resolved,
    InsufficientVehicleAssignment,
    AmbiguousVehicleAssignment,
}

public sealed record VehicleAssignmentResolution(
    VehicleAssignmentResolutionStatus Status,
    DateTimeOffset At,
    string TechnicianExternalId,
    string? ObjectId,
    IReadOnlyList<TechnicianVehicleAssignment> Assignments,
    string Reason);

public sealed record PowerfleetVehicleObservation(
    string ObjectId,
    string? RegistrationPlate,
    string Name,
    string? Make,
    string? Model,
    bool IsActive = true);

public sealed record VehicleAssignmentBackfillRequest(
    string TechnicianCode,
    string ObjectId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    string Source,
    string EvidenceReference,
    string Actor);

public enum TechnicianTrackingStatus
{
    TrackAndTraceAvailable,
    NoTrackAndTrace,
}

public sealed class TechnicianTrackingEligibility
{
    public int Id { get; set; }
    public string TechnicianExternalId { get; set; } = string.Empty;
    public string TechnicianCode { get; set; } = string.Empty;
    public TechnicianTrackingStatus TrackingStatus { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public bool IsValidAt(DateTimeOffset instant) =>
        ValidFrom <= instant && (ValidTo is null || instant < ValidTo);
}

public sealed class VehicleAssignmentSyncRun
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public int VehiclesRead { get; set; }
    public int PhysicalVehiclesObserved { get; set; }
    public int ExactMapped { get; set; }
    public int AssignmentsOpened { get; set; }
    public int AssignmentsObserved { get; set; }
    public int AssignmentsClosed { get; set; }
    public int Ambiguous { get; set; }
    public int Unmapped { get; set; }
    public int ResourcesWithoutPersonalVehicle { get; set; }
    public int SkippedNoTrackAndTrace { get; set; }
    public string? ErrorSummary { get; set; }
}

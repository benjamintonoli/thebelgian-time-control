namespace TheBelgian.TimeControl.Core.Models;

public sealed class DailyTechnicianTimeline
{
    public required string TechnicianExternalId { get; init; }
    public required string TechnicianName { get; init; }
    public required DateOnly Date { get; init; }
    public DateTimeOffset? PlenionStart { get; init; }
    public DateTimeOffset? PlenionEnd { get; init; }
    public int RegisteredMinutes { get; init; }
    public int BreakMinutes { get; init; }
    public decimal RegisteredKilometres { get; init; }
    public int RegisteredTravelMinutes { get; init; }
    public DateTimeOffset? FirstTripStart { get; init; }
    public DateTimeOffset? LastTripEnd { get; init; }
    public int DrivingMinutes { get; init; }
    public decimal PowerfleetDistanceKilometres { get; init; }
    public bool HasCertainVehicleAssignment { get; init; } = true;
}

public sealed class DetectedException
{
    public int Id { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string TechnicianExternalId { get; set; } = string.Empty;
    public string TechnicianName { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public ExceptionType Type { get; set; }
    public int DifferenceMinutes { get; set; }
    public ExceptionPriority Priority { get; set; }
    public string Reason { get; set; } = string.Empty;
    public ReviewDecision ReviewDecision { get; set; }
    public DateTimeOffset? PlenionStart { get; set; }
    public DateTimeOffset? PlenionEnd { get; set; }
    public int RegisteredMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public DateTimeOffset? FirstTripStart { get; set; }
    public DateTimeOffset? LastTripEnd { get; set; }
    public int DrivingMinutes { get; set; }
    public decimal PowerfleetDistanceKilometres { get; set; }
    public int StartDifferenceMinutes { get; set; }
    public int EndDifferenceMinutes { get; set; }
    public int TravelDifferenceMinutes { get; set; }
    public int IgnoreToleranceMinutes { get; set; }
    public int IndividualToleranceMinutes { get; set; }
    public int HighPriorityToleranceMinutes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastCalculatedAt { get; set; }
}

public enum ExceptionType
{
    None,
    RegisteredStartTooEarly,
    RegisteredEndTooLate,
    RegisteredTravelExceedsPowerfleet,
    StructuralPattern,
    InsufficientPowerfleetData,
    UncertainVehicleAssignment,
    ManualReviewRequired,
}

public enum ExceptionPriority
{
    Low,
    Normal,
    High,
}

public enum ReviewDecision
{
    Unreviewed,
    CorrectRegistration,
    ManualReviewRequired,
    InsufficientGpsData,
    VehicleChange,
    ExceptionConfirmed,
}

public sealed class SynchronizationRun
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ThroughDate { get; set; }
    public int ImportedPlenionCount { get; set; }
    public int ImportedPowerfleetCount { get; set; }
    public int DetectedExceptionCount { get; set; }
    public string Status { get; set; } = "Started";
    public string? ErrorMessage { get; set; }
}

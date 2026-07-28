namespace TheBelgian.TimeControl.Core.Models;

public sealed record BroaderValidationRequest(
    IReadOnlyList<BroaderValidationTechnicianRequest> Technicians,
    DateOnly FromDate,
    DateOnly ThroughDate,
    int MaxWorkingDaysPerTechnician = 5);

public sealed record BroaderValidationTechnicianRequest(
    string TechnicianQuery,
    string? PowerfleetDriverId = null);

public sealed class BroaderValidationResult
{
    public required DateOnly FromDate { get; init; }
    public required DateOnly ThroughDate { get; init; }
    public required IReadOnlyList<BroaderValidationTechnicianResult> Technicians { get; init; }
    public required BroaderValidationSummary Summary { get; init; }
    public required IReadOnlyList<string> Observations { get; init; }
}

public sealed class BroaderValidationTechnicianResult
{
    public required string Query { get; init; }
    public required bool Processed { get; init; }
    public string? SkipReason { get; init; }
    public Technician? Technician { get; init; }
    public string? DriverId { get; init; }
    public string? DriverName { get; init; }
    public required IReadOnlyList<BroaderValidationDayResult> Days { get; init; }
    public required IReadOnlyList<PilotIssue> Issues { get; init; }
    public ReadOnlyPilotResult? PilotResult { get; init; }
}

public sealed record BroaderValidationDayResult(
    DateOnly Date,
    string TechnicianName,
    string? DriverId,
    string? DriverName,
    IReadOnlyList<BroaderValidationVehicleContext> Vehicles,
    PilotWorkLocationCandidate? FirstWorkLocation,
    DateTimeOffset? FirstPlenionStart,
    DateTimeOffset? LastPlenionEnd,
    DateTimeOffset? LastWorkLocationDeparture,
    int? StartDifferenceMinutes,
    int? EndDifferenceMinutes,
    bool StartDifferenceRelevant,
    bool EndDifferenceRelevant,
    int PossibleEmployeeBenefitMinutes,
    bool StartExceedsIndividualTolerance,
    bool EndExceedsIndividualTolerance,
    bool StartExceedsHighPriorityTolerance,
    bool EndExceedsHighPriorityTolerance,
    int PlenionPerformanceCount,
    int LinkedCustomerStopCount,
    int ConfirmedLocationMatchCount,
    int ProbableLocationMatchCount,
    int ManualReviewRequiredCount,
    int NoReliableMatchCount,
    int AddressDataIssueCount,
    int MissingDriverTripCount,
    string DataQuality,
    string DayType,
    string Notes);

public sealed record BroaderValidationVehicleContext(
    string? ObjectId,
    string? ObjectName,
    string? ObjectPlate);

public sealed class BroaderValidationSummary
{
    public int ProcessedTechnicianCount { get; init; }
    public int SkippedTechnicianCount { get; init; }
    public int WorkdayCount { get; init; }
    public int TotalPerformanceCount { get; init; }
    public int TotalLocationResolutionCount { get; init; }
    public int ConfirmedLocationMatchCount { get; init; }
    public int ProbableLocationMatchCount { get; init; }
    public int ManualReviewRequiredCount { get; init; }
    public int NoReliableMatchCount { get; init; }
    public int AddressDataIssueCount { get; init; }
    public double ConfirmedPercent { get; init; }
    public double ProbablePercent { get; init; }
    public double ManualReviewPercent { get; init; }
    public double ReliableMatchPercent { get; init; }
    public int MissingDriverTripCount { get; init; }
    public int PossibleHourDeviationCount { get; init; }
    public int IndividualToleranceDeviationCount { get; init; }
    public int HighPriorityToleranceDeviationCount { get; init; }
    public required IReadOnlyList<string> RecurringAddressProblems { get; init; }
    public required IReadOnlyList<string> SkippedTechnicians { get; init; }
}

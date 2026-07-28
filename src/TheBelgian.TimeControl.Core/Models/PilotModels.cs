namespace TheBelgian.TimeControl.Core.Models;

public sealed record ReadOnlyPilotRequest(
    string TechnicianQuery,
    DateOnly FromDate,
    DateOnly ThroughDate,
    string? PowerfleetDriverId = null,
    string? PowerfleetObjectId = null,
    string? VehiclePlate = null,
    IReadOnlyList<PilotAbsence>? Absences = null,
    bool DriverOnlyLinking = false,
    bool ResolveAllLocations = false,
    int MaxWorkingDays = 3,
    int? MaximumPerformances = null,
    int? MaximumTrips = null,
    IReadOnlyList<DateOnly>? SelectedWorkdays = null);

public sealed record PilotAbsence(
    DateOnly Date,
    string Type,
    string Reason);

public sealed class ReadOnlyPilotResult
{
    public required Technician Technician { get; init; }
    public required DateOnly FromDate { get; init; }
    public required DateOnly ThroughDate { get; init; }
    public required IReadOnlyList<PilotRawRecord> RawPlenionRecords { get; init; }
    public required IReadOnlyList<PilotRawRecord> RawPowerfleetRecords { get; init; }
    public required IReadOnlyList<NormalizedPilotPerformance> PlenionRecords { get; init; }
    public required IReadOnlyList<NormalizedPilotTrip> PowerfleetRecords { get; init; }
    public required IReadOnlyList<PilotStop> PowerfleetStops { get; init; }
    public required IReadOnlyList<PilotPerformanceStopMatch> PerformanceStopMatches { get; init; }
    public required IReadOnlyList<PilotDayComparison> DayComparisons { get; init; }
    public required IReadOnlyList<PilotIssue> Issues { get; init; }
    public required IReadOnlyList<string> SourceObservations { get; init; }
    public int PlenionReadCount { get; init; }
    public int PlenionRejectedCount { get; init; }
    public int PowerfleetReadCount { get; init; }
    public int PowerfleetRejectedCount { get; init; }
    public int PowerfleetMatchedCount { get; init; }
    public string? PowerfleetEndpoint { get; init; }
    public required string PowerfleetFilterSummary { get; init; }
    public int IgnoreDifferenceMinutes { get; init; }
    public int PatternDifferenceMinutes { get; init; }
    public int IndividualExceptionMinutes { get; init; }
    public int HighPriorityExceptionMinutes { get; init; }
    public required IReadOnlyList<PilotLocationResolution> LocationResolutions { get; init; }
    public double StrongLocationMatchMeters { get; init; }
    public double PossibleLocationMatchMeters { get; init; }
    public required string GeocodingProvider { get; init; }
    public bool GeocodingConfigured { get; init; }
}

public sealed record PilotRawRecord(
    string SourceId,
    IReadOnlyDictionary<string, PilotRawValue> Fields);

public sealed record PilotRawValue(string? Text, string SourceType);

public sealed record NormalizedPilotPerformance(
    long ExternalId,
    string ResourceExternalId,
    DateOnly Date,
    DateTimeOffset StartDateTime,
    DateTimeOffset EndDateTime,
    int PauseMinutes,
    int GrossMinutes,
    int NetMinutes,
    decimal DistanceKilometres,
    string? ProjectExternalId,
    string? MainTaskExternalId,
    string? WorkOrderNumber,
    string? Description,
    string? Comment,
    string? ProjectNumber,
    string? ProjectName,
    string? DeliveryAddressExternalId,
    string? CustomerOrSiteName,
    string? Street,
    string? PostalCode,
    string? City,
    string? Country,
    int ProjectCandidateCount,
    int WorkOrderCandidateCount,
    int AddressCandidateCount,
    string JoinAssessment,
    string Normalization);

public sealed record NormalizedPilotTrip(
    string ExternalId,
    DateTimeOffset StartDateTime,
    DateTimeOffset EndDateTime,
    int DrivingMinutes,
    int? StoppedAfterMinutes,
    decimal DistanceKilometres,
    string? DriverId,
    string? DriverName,
    string? ObjectId,
    string? ObjectName,
    string? VehiclePlate,
    string? StartLocation,
    string? StartAddress,
    string? StartArea,
    string? StartAreaGroup,
    string? EndLocation,
    string? EndAddress,
    string? EndArea,
    string? EndAreaGroup,
    decimal? StartLatitude,
    decimal? StartLongitude,
    decimal? EndLatitude,
    decimal? EndLongitude,
    string Normalization);

public sealed record PilotStop(
    string StopId,
    DateOnly Date,
    string IncomingTripId,
    string OutgoingTripId,
    DateTimeOffset Arrival,
    DateTimeOffset Departure,
    int DurationMinutes,
    string? Address,
    string? PostalCode,
    string? City,
    string? Street,
    string? Area,
    string? AreaGroup,
    decimal? Latitude,
    decimal? Longitude,
    string? VehiclePlate,
    string? DriverId,
    string? DriverName,
    bool LocationContinuity,
    string ContinuityAssessment);

public enum PilotMatchStatus
{
    ExactAddressMatch,
    ProbableAddressMatch,
    TimeOnlyMatch,
    Ambiguous,
    NoMatch,
}

public sealed record PilotMatchAlternative(
    string StopId,
    DateTimeOffset Arrival,
    DateTimeOffset Departure,
    string? Address,
    int Score,
    PilotMatchStatus Status,
    string Reasons);

public sealed record PilotPerformanceStopMatch(
    long PerformanceId,
    DateOnly Date,
    DateTimeOffset RegisteredStart,
    DateTimeOffset RegisteredEnd,
    string? ExpectedAddress,
    string? ExpectedPostalCode,
    string? ExpectedCity,
    PilotStop? MatchedStop,
    string AddressComparison,
    int TimeOverlapMinutes,
    PilotMatchStatus Status,
    int ConfidenceScore,
    string Reasons,
    IReadOnlyList<PilotMatchAlternative> Alternatives);

public sealed record PilotDayComparison(
    DateOnly Date,
    string Technician,
    bool IsAbsent,
    string? AbsenceReason,
    DateTimeOffset? FirstPlenionStart,
    DateTimeOffset? LastPlenionEnd,
    int TotalPlenionNetMinutes,
    decimal TotalPlenionKilometres,
    PilotLocationContext? HomeDeparture,
    PilotLocationContext? HomeArrival,
    PilotWorkLocationCandidate? FirstWorkLocation,
    PilotWorkLocationCandidate? LastWorkLocation,
    int TotalPowerfleetDrivingMinutes,
    decimal TotalPowerfleetDistanceKilometres,
    int? StartDifferenceMinutes,
    int? EndDifferenceMinutes,
    bool StartDifferenceRelevant,
    bool EndDifferenceRelevant,
    int PossibleEmployeeBenefitMinutes,
    string DataQuality,
    string Notes);

public sealed record PilotLocationContext(
    DateTimeOffset Timestamp,
    string? Address,
    string? Area,
    string? AreaGroup,
    string TripId);

public sealed record PilotWorkLocationCandidate(
    DateTimeOffset Timestamp,
    string? Address,
    string? Area,
    string? AreaGroup,
    string TripId,
    bool Reliable,
    string Assessment);

public sealed record PilotIssue(
    string Source,
    string? RecordId,
    string Category,
    string Message);

public enum PilotDistanceClassification
{
    StrongLocationMatch,
    PossibleLocationMatch,
    LocationMismatch,
}

public enum PilotLocationResolutionStatus
{
    ConfirmedLocationMatch,
    ProbableLocationMatch,
    ManualReviewRequired,
    AddressDataIssue,
    NoReliableMatch,
}

public sealed record PilotLocationCandidateScore(
    PilotStop Stop,
    double? DistanceMeters,
    PilotDistanceClassification? DistanceClassification,
    int TimeOverlapMinutes,
    int StartDifferenceMinutes,
    int EndDifferenceMinutes,
    int AddressScore,
    int DistanceScore,
    int TimeScore,
    int TotalScore,
    PilotLocationResolutionStatus MatchStatus,
    string Explanation);

public sealed record PilotLocationResolution(
    long PerformanceId,
    DateOnly Date,
    string? ProjectNumber,
    string? ProjectName,
    string? WorkOrderNumber,
    DateTimeOffset RegisteredStart,
    DateTimeOffset RegisteredEnd,
    string? DeliveryAddressExternalId,
    string OriginalAddress,
    string NormalizedAddress,
    string AddressHash,
    GeocodingResult Geocoding,
    IReadOnlyList<PilotLocationCandidateScore> Candidates,
    PilotLocationResolutionStatus MatchStatus,
    string DiagnosticCategory,
    string Assessment);

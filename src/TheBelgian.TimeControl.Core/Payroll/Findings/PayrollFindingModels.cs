namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Deterministic payroll review finding. No AI decisions; evidence-only.
/// </summary>
public sealed record PayrollFinding(
    string FindingKey,
    string ResourceId,
    DateOnly Date,
    PayrollFindingType FindingType,
    PayrollFindingSeverity Severity,
    string Title,
    string Description,
    string Evidence,
    string SuggestedAction,
    IReadOnlyList<long> RelatedPerformanceIds,
    decimal? PlannedHours = null,
    decimal? BookedHours = null,
    decimal? OverlapHours = null,
    decimal? SuggestedOvertimeAdjustmentHours = null,
    decimal? LegacyDifferenceHours = null,
    DateTimeOffset? SuggestedPayableStart = null,
    DateTimeOffset? SuggestedPayableEnd = null,
    decimal? SuggestedPayableHours = null,
    string? SuggestedProjectId = null,
    string? SuggestedBonNr = null,
    string? GpsClassification = null);

public sealed record PayrollPlanningReservation(
    long IdCalendar,
    string ResourceId,
    DateOnly Date,
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    int TaskTypeId,
    string? TaskTypeName,
    string? ProjectId,
    int? ProjectNumber,
    int? HfdTaakId,
    string? Subject,
    PayrollPlanningClassification Classification)
{
    public decimal? PlannedHours
    {
        get
        {
            if (TimeFrom is null || TimeTo is null || TimeTo <= TimeFrom)
            {
                return null;
            }

            return (decimal)(TimeTo.Value.ToTimeSpan() - TimeFrom.Value.ToTimeSpan()).TotalHours;
        }
    }
}

public sealed record StandbyGpsTripEvidence(
    string TripId,
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal DistanceKilometres,
    int DrivingMinutes,
    string? StartAddress,
    string? EndAddress,
    string? ObjectId,
    string? VehiclePlate,
    decimal? StartLatitude = null,
    decimal? StartLongitude = null,
    decimal? EndLatitude = null,
    decimal? EndLongitude = null);

public sealed record StandbyGpsDayEvidence(
    string ResourceId,
    DateOnly Date,
    bool HasVehicleMapping,
    bool MappingAmbiguous,
    string? ObjectId,
    string? RegistrationPlate,
    string MappingReason,
    IReadOnlyList<StandbyGpsTripEvidence> Trips,
    string MappingKind = "None",
    string? UnmappedReasonCode = null)
{
    public bool HasUsableTrips => Trips.Count > 0;
}

public sealed record StandbyGpsBatchResult(
    IReadOnlyList<StandbyGpsDayEvidence> Days,
    int ApiCallCount,
    int MappedResources,
    int UnmappedResources,
    int ResourcesWithGps,
    int ResourcesWithoutGps,
    string Notes);

public sealed record PayrollFindingsRunResult(
    IReadOnlyList<PayrollFinding> Findings,
    int QueryCount,
    string PlanningSourceNotes,
    int GpsQueryCount = 0,
    string? GpsSourceNotes = null);

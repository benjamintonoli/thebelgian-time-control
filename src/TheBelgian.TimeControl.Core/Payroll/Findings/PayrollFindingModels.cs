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
    decimal? LegacyDifferenceHours = null);

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

public sealed record PayrollFindingsRunResult(
    IReadOnlyList<PayrollFinding> Findings,
    int QueryCount,
    string PlanningSourceNotes);

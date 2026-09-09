using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Findings;

public static class PayrollFindingsEngine
{
    public const string PlanningSourceNotes =
        "KALENDER work reservations via IDPROJ/PROJNR + resource/date/time. "
        + "Absence types 3/5/8/10/39 and standby type 36 never support project 200/300. "
        + "Type 9 (Opleiding) and text markers identify project-100 training. "
        + "Unknown task types are Ambiguous evidence.";

    public static PayrollFindingsRunResult Evaluate(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<PayrollPlanningReservation> planning,
        IReadOnlyDictionary<string, decimal?> legacyDifferenceByResource,
        int planningQueryCount,
        IReadOnlyList<StandbyGpsDayEvidence>? standbyGps = null,
        int gpsQueryCount = 0,
        string? gpsSourceNotes = null,
        IReadOnlySet<string>? includedResourceIds = null)
    {
        var special = SpecialProjectTimeControl.Evaluate(performances, planning, legacyDifferenceByResource);
        var overlaps = OverlapControl.Evaluate(performances, standbyGps);
        var standby = StandbyControl.Evaluate(performances, planning, standbyGps ?? []);
        var included = includedResourceIds
            ?? performances.Select(item => item.ResourceId)
                .Concat(planning.Select(item => item.ResourceId))
                .ToHashSet(StringComparer.Ordinal);
        var missing = MissingTechnicianControl.Evaluate(
            performances,
            planning,
            standbyGps ?? [],
            included);
        var findings = special.Concat(overlaps).Concat(standby).Concat(missing)
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenByDescending(item => item.Severity)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();

        return new PayrollFindingsRunResult(
            findings,
            planningQueryCount + gpsQueryCount,
            PlanningSourceNotes,
            gpsQueryCount,
            gpsSourceNotes);
    }

    public static PayrollFindingRecord ToRecord(int shadowMonthId, PayrollFinding finding) =>
        new()
        {
            ShadowMonthId = shadowMonthId,
            FindingKey = finding.FindingKey,
            ResourceId = finding.ResourceId,
            Date = finding.Date,
            FindingType = finding.FindingType,
            Severity = finding.Severity,
            Status = PayrollFindingStatus.Open,
            Title = finding.Title,
            Description = finding.Description,
            Evidence = finding.Evidence,
            SuggestedAction = finding.SuggestedAction,
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(finding.RelatedPerformanceIds),
            PlannedHours = finding.PlannedHours,
            BookedHours = finding.BookedHours,
            OverlapHours = finding.OverlapHours,
            SuggestedOvertimeAdjustmentHours = finding.SuggestedOvertimeAdjustmentHours,
            LegacyDifferenceHours = finding.LegacyDifferenceHours,
            SuggestedPayableStart = finding.SuggestedPayableStart,
            SuggestedPayableEnd = finding.SuggestedPayableEnd,
            SuggestedPayableHours = finding.SuggestedPayableHours,
            SuggestedProjectId = finding.SuggestedProjectId,
            SuggestedBonNr = finding.SuggestedBonNr,
            GpsClassification = finding.GpsClassification,
        };
}

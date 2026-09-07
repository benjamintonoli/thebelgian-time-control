using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Special-project findings for PROJNR 100/200/300. Does not mutate payroll totals.
/// </summary>
public static class SpecialProjectTimeControl
{
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static IReadOnlyList<PayrollFinding> Evaluate(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<PayrollPlanningReservation> planning,
        IReadOnlyDictionary<string, decimal?> legacyDifferenceByResource)
    {
        var findings = new List<PayrollFinding>();
        var planningByResourceDate = planning
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToDictionary(group => group.Key, group => group.ToList());

        var trainingByResource = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var performance in performances.Where(item => !item.IsCalendarSynthetic))
        {
            if (!PayrollPlanningClassifier.IsSpecialProjectNumber(performance.ProjectNumber))
            {
                continue;
            }

            planningByResourceDate.TryGetValue((performance.ResourceId, performance.Date), out var dayPlanning);
            dayPlanning ??= [];

            var matching = FindMatchingReservations(performance, dayPlanning);
            var supporting = matching
                .Where(PayrollPlanningClassifier.CanSupportSpecialProjectWork)
                .ToList();

            if (performance.ProjectNumber == 300)
            {
                if (supporting.Count == 0)
                {
                    findings.Add(CreateMissingPlanningFinding(
                        performance,
                        PayrollFindingType.Project300WithoutPlanning,
                        "Project 300 zonder planning",
                        "Controleer waarom project 300 werd geboekt zonder planning/reservatie."));
                }
            }
            else if (performance.ProjectNumber == 200)
            {
                if (supporting.Count == 0)
                {
                    findings.Add(CreateMissingPlanningFinding(
                        performance,
                        PayrollFindingType.Project200WithoutPlanning,
                        "Project 200 zonder planning",
                        "Controleer waarom project 200 werd geboekt zonder relevante reservatie."));
                }
                else
                {
                    var durationFinding = CreateDurationOverrunFinding(
                        performance,
                        supporting,
                        PayrollFindingType.Project200ExceedsPlanning,
                        "Project 200 langer dan planning");
                    if (durationFinding is not null)
                    {
                        findings.Add(durationFinding);
                    }
                }
            }
            else if (performance.ProjectNumber == 100)
            {
                var isTraining = PayrollPlanningClassifier.IsTrainingPerformance(performance, matching);
                if (!isTraining)
                {
                    continue;
                }

                var booked = BookedHours(performance);
                trainingByResource[performance.ResourceId] =
                    trainingByResource.GetValueOrDefault(performance.ResourceId) + booked;

                findings.Add(new PayrollFinding(
                    FindingKey: $"p100-training:{performance.ResourceId}:{performance.Date:yyyyMMdd}:{performance.SourceEntryId}",
                    ResourceId: performance.ResourceId,
                    Date: performance.Date,
                    FindingType: PayrollFindingType.Project100TrainingHours,
                    Severity: PayrollFindingSeverity.Info,
                    Title: "Toolbox/opleiding uren",
                    Description: $"Project 100 training/toolbox geboekt: {FormatHours(booked)}.",
                    Evidence: BuildTrainingEvidence(performance, matching),
                    SuggestedAction: "Training/toolbox mag geen overuren genereren; controleer impact op verschil.",
                    RelatedPerformanceIds: [performance.SourceEntryId],
                    BookedHours: booked));

                var durationFinding = CreateDurationOverrunFinding(
                    performance,
                    matching.Where(PayrollPlanningClassifier.CanSupportSpecialProjectWork).ToList(),
                    PayrollFindingType.Project100ExceedsPlannedDuration,
                    "Toolbox langer dan gepland");
                if (durationFinding is not null)
                {
                    findings.Add(durationFinding);
                }
            }
        }

        foreach (var (resourceId, trainingHours) in trainingByResource)
        {
            legacyDifferenceByResource.TryGetValue(resourceId, out var legacyDiff);
            if (legacyDiff is null || legacyDiff <= 0m || trainingHours <= 0m)
            {
                continue;
            }

            var contribution = Math.Min(trainingHours, legacyDiff.Value);
            var target = legacyDiff.Value - contribution;
            findings.Add(new PayrollFinding(
                FindingKey: $"p100-ot:{resourceId}:{legacyDiff.Value:0.00}:{trainingHours:0.00}",
                ResourceId: resourceId,
                Date: performances
                    .Where(item => item.ResourceId == resourceId && item.ProjectNumber == 100)
                    .Select(item => item.Date)
                    .DefaultIfEmpty(DateOnly.MinValue)
                    .Max(),
                FindingType: PayrollFindingType.Project100TrainingInOvertime,
                Severity: PayrollFindingSeverity.High,
                Title: "Toolbox veroorzaakt overuren",
                Description:
                    $"Legacy verschil {FormatHours(legacyDiff.Value)}; trainingbijdrage {FormatHours(contribution)}; "
                    + $"voorstel betaalbare overuren {FormatHours(target)}.",
                Evidence:
                    $"Traininguren maand={FormatHours(trainingHours)}. LegacyDifferenceHours ongewijzigd bewaard.",
                SuggestedAction:
                    $"Toolbox veroorzaakt {FormatHours(contribution)} overuren; doelvoorstel = {FormatHours(target)}.",
                RelatedPerformanceIds: performances
                    .Where(item => item.ResourceId == resourceId && item.ProjectNumber == 100)
                    .Select(item => item.SourceEntryId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray(),
                BookedHours: trainingHours,
                SuggestedOvertimeAdjustmentHours: target,
                LegacyDifferenceHours: legacyDiff));
        }

        return findings
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
    }

    private static List<PayrollPlanningReservation> FindMatchingReservations(
        NormalizedPerformanceEntry performance,
        IReadOnlyList<PayrollPlanningReservation> dayPlanning) =>
        dayPlanning
            .Where(item =>
                item.ProjectNumber == performance.ProjectNumber
                || (performance.ProjectId is not null
                    && string.Equals(item.ProjectId, performance.ProjectId, StringComparison.Ordinal))
                || (performance.ProjectNumber == 100
                    && PayrollPlanningClassifier.IsTrainingTaskType(item.TaskTypeId)))
            .Where(item => TimesOverlapOrDateOnly(performance, item))
            .ToList();

    private static bool TimesOverlapOrDateOnly(
        NormalizedPerformanceEntry performance,
        PayrollPlanningReservation reservation)
    {
        if (performance.Start is null || performance.End is null
            || reservation.TimeFrom is null || reservation.TimeTo is null)
        {
            return true;
        }

        var perfStart = TimeOnly.FromTimeSpan(performance.Start.Value.TimeOfDay);
        var perfEnd = TimeOnly.FromTimeSpan(performance.End.Value.TimeOfDay);
        return reservation.TimeFrom < perfEnd && reservation.TimeTo > perfStart;
    }

    private static PayrollFinding CreateMissingPlanningFinding(
        NormalizedPerformanceEntry performance,
        PayrollFindingType type,
        string title,
        string action) =>
        new(
            FindingKey: $"{type}:{performance.ResourceId}:{performance.Date:yyyyMMdd}:{performance.SourceEntryId}",
            ResourceId: performance.ResourceId,
            Date: performance.Date,
            FindingType: type,
            Severity: PayrollFindingSeverity.Review,
            Title: title,
            Description:
                $"Geboekt {FormatInterval(performance)} ({FormatHours(BookedHours(performance))}) zonder ondersteunende planning.",
            Evidence: BuildMissingPlanningEvidence(performance),
            SuggestedAction: action,
            RelatedPerformanceIds: [performance.SourceEntryId],
            BookedHours: BookedHours(performance),
            SuggestedProjectId: performance.ProjectId
                ?? performance.ProjectNumber?.ToString(CultureInfo.InvariantCulture),
            SuggestedBonNr: performance.BonNr);

    private static string BuildMissingPlanningEvidence(NormalizedPerformanceEntry performance)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"PerformanceId={performance.SourceEntryId}; PROJNR={performance.ProjectNumber}; ");
        sb.Append(CultureInfo.InvariantCulture, $"desc={performance.Description ?? "—"}; memo={performance.Memo ?? "—"}; ");
        sb.Append("planning evidence = none.");
        return sb.ToString();
    }

    private static PayrollFinding? CreateDurationOverrunFinding(
        NormalizedPerformanceEntry performance,
        IReadOnlyList<PayrollPlanningReservation> supporting,
        PayrollFindingType type,
        string title)
    {
        var planned = supporting
            .Select(item => item.PlannedHours)
            .Where(hours => hours is not null)
            .Select(hours => hours!.Value)
            .DefaultIfEmpty()
            .Max();
        if (planned <= 0m)
        {
            return null;
        }

        var booked = BookedHours(performance);
        var difference = booked - planned;
        if (difference <= 0m)
        {
            return null;
        }

        return new PayrollFinding(
            FindingKey: $"{type}:{performance.ResourceId}:{performance.Date:yyyyMMdd}:{performance.SourceEntryId}",
            ResourceId: performance.ResourceId,
            Date: performance.Date,
            FindingType: type,
            Severity: PayrollFindingSeverity.Review,
            Title: title,
            Description:
                $"Geboekt {FormatHours(booked)} vs gepland {FormatHours(planned)} (verschil +{FormatHours(difference)}).",
            Evidence:
                $"PerformanceId={performance.SourceEntryId}; planningIds={string.Join(',', supporting.Select(item => item.IdCalendar))}.",
            SuggestedAction: "Controleer waarom langer geboekt werd dan de planning.",
            RelatedPerformanceIds: [performance.SourceEntryId],
            PlannedHours: planned,
            BookedHours: booked);
    }

    private static string BuildTrainingEvidence(
        NormalizedPerformanceEntry performance,
        List<PayrollPlanningReservation> matching)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"PerformanceId={performance.SourceEntryId}; HFDTAAK={performance.HfdTaakId}; ");
        sb.Append(CultureInfo.InvariantCulture, $"desc={performance.Description ?? "—"}; memo={performance.Memo ?? "—"}; ");
        if (matching.Count == 0)
        {
            sb.Append("planning=none");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"planning=[{string.Join("; ", matching.Select(item => $"{item.IdCalendar}/{item.TaskTypeId}/{item.Classification}"))}]");
        }

        return sb.ToString();
    }

    private static decimal BookedHours(NormalizedPerformanceEntry performance)
    {
        if (performance.AtlHoursRaw > 0m)
        {
            return performance.AtlHoursRaw;
        }

        if (performance.Start is not null && performance.End is not null && performance.End > performance.Start)
        {
            return (decimal)(performance.End.Value - performance.Start.Value).TotalHours;
        }

        return 0m;
    }

    private static string FormatInterval(NormalizedPerformanceEntry performance) =>
        performance.Start is null || performance.End is null
            ? "—"
            : $"{performance.Start:HH:mm}-{performance.End:HH:mm}";

    private static string FormatHours(decimal hours) =>
        hours.ToString("0.00", Belgian) + " u";
}

using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Findings;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollFindingsControlTests
{
    private static readonly DateOnly Day = new(2026, 8, 12);

    [Fact]
    public void Project300_MatchingPlanning_NoFinding()
    {
        var performance = Performance(1, "10", 300, "08:00", "12:00", hours: 4m);
        var planning = Reservation("10", 300, taskTypeId: 26, "08:00", "12:00");

        var findings = SpecialProjectTimeControl.Evaluate([performance], [planning], EmptyDiff());

        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.Project300WithoutPlanning);
    }

    [Fact]
    public void Project300_NoPlanning_CreatesFinding()
    {
        var performance = Performance(1, "10", 300, "08:00", "12:00", hours: 4m);

        var findings = SpecialProjectTimeControl.Evaluate([performance], [], EmptyDiff());

        var finding = Assert.Single(findings, item => item.FindingType == PayrollFindingType.Project300WithoutPlanning);
        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Contains("planning evidence = none", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("desc=", finding.Evidence, StringComparison.Ordinal);
        Assert.Equal("P300", finding.SuggestedProjectId);
        Assert.Equal("Controleer waarom project 300 werd geboekt zonder planning/reservatie.", finding.SuggestedAction);
    }

    [Fact]
    public void Project300_PlanningForOtherResource_CreatesFinding()
    {
        var performance = Performance(1, "10", 300, "08:00", "12:00", hours: 4m);
        var planning = Reservation("99", 300, taskTypeId: 26, "08:00", "12:00");

        var findings = SpecialProjectTimeControl.Evaluate([performance], [planning], EmptyDiff());

        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.Project300WithoutPlanning);
    }

    [Fact]
    public void Project300_AbsenceCalendar_DoesNotSupport()
    {
        var performance = Performance(1, "10", 300, "08:00", "12:00", hours: 4m);
        var planning = Reservation("10", 300, taskTypeId: 3, "08:00", "12:00");

        var findings = SpecialProjectTimeControl.Evaluate([performance], [planning], EmptyDiff());

        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.Project300WithoutPlanning);
        Assert.Equal(PayrollPlanningClassification.Absence, planning.Classification);
    }

    [Fact]
    public void Project200_MatchingPlanning_NoMissingFinding()
    {
        var performance = Performance(2, "11", 200, "08:00", "10:00", hours: 2m);
        var planning = Reservation("11", 200, taskTypeId: 26, "08:00", "10:00");

        var findings = SpecialProjectTimeControl.Evaluate([performance], [planning], EmptyDiff());

        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.Project200WithoutPlanning);
        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.Project200ExceedsPlanning);
    }

    [Fact]
    public void Project200_NoPlanning_CreatesFinding()
    {
        var performance = Performance(2, "11", 200, "08:00", "10:00", hours: 2m);

        var findings = SpecialProjectTimeControl.Evaluate([performance], [], EmptyDiff());

        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.Project200WithoutPlanning);
    }

    [Fact]
    public void Project200_BookedLongerThanPlanned_CreatesDurationFinding()
    {
        var performance = Performance(2, "11", 200, "08:00", "12:00", hours: 4m);
        var planning = Reservation("11", 200, taskTypeId: 26, "08:00", "10:00");

        var findings = SpecialProjectTimeControl.Evaluate([performance], [planning], EmptyDiff());

        var finding = Assert.Single(findings, item => item.FindingType == PayrollFindingType.Project200ExceedsPlanning);
        Assert.Equal(2m, finding.PlannedHours);
        Assert.Equal(4m, finding.BookedHours);
    }

    [Fact]
    public void Project100_TrainingWithoutOvertime_InfoOnly()
    {
        var performance = Performance(
            3,
            "12",
            100,
            "08:00",
            "10:00",
            hours: 2m,
            hfdTaakId: 21,
            description: "Toolbox");

        var findings = SpecialProjectTimeControl.Evaluate(
            [performance],
            [],
            new Dictionary<string, decimal?> { ["12"] = 0m });

        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.Project100TrainingHours);
        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.Project100TrainingInOvertime);
        Assert.All(
            findings.Where(item => item.FindingType == PayrollFindingType.Project100TrainingHours),
            item => Assert.Equal(PayrollFindingSeverity.Info, item.Severity));
    }

    [Fact]
    public void Project100_TrainingInOvertime_ProposesTargetWithoutChangingLegacy()
    {
        var performance = Performance(
            3,
            "12",
            100,
            "08:00",
            "10:00",
            hours: 2m,
            hfdTaakId: 21,
            description: "Opleiding");
        var legacyDiff = new Dictionary<string, decimal?> { ["12"] = 3m };

        var findings = SpecialProjectTimeControl.Evaluate([performance], [], legacyDiff);

        var overtime = Assert.Single(findings, item => item.FindingType == PayrollFindingType.Project100TrainingInOvertime);
        Assert.Equal(PayrollFindingSeverity.High, overtime.Severity);
        Assert.Equal(3m, overtime.LegacyDifferenceHours);
        Assert.Equal(1m, overtime.SuggestedOvertimeAdjustmentHours);
        Assert.Equal(3m, legacyDiff["12"]);
    }

    [Fact]
    public void Project100_BookedExceedsPlanned_CreatesDurationFinding()
    {
        var performance = Performance(
            3,
            "12",
            100,
            "08:00",
            "12:00",
            hours: 4m,
            hfdTaakId: 21,
            description: "Toolbox");
        var planning = Reservation("12", 100, taskTypeId: 9, "08:00", "10:00");

        var findings = SpecialProjectTimeControl.Evaluate([performance], [planning], EmptyDiff());

        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.Project100ExceedsPlannedDuration);
    }

    [Fact]
    public void Project100_UnrelatedWork_NotClassifiedAsTraining()
    {
        var performance = Performance(
            3,
            "12",
            100,
            "08:00",
            "10:00",
            hours: 2m,
            hfdTaakId: 1,
            description: "Interne administratie");

        var findings = SpecialProjectTimeControl.Evaluate(
            [performance],
            [],
            new Dictionary<string, decimal?> { ["12"] = 5m });

        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.Project100TrainingHours);
        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.Project100TrainingInOvertime);
    }

    [Fact]
    public void Overlap_None_NoFinding()
    {
        var left = Performance(10, "20", 50, "08:00", "10:00", hours: 2m);
        var right = Performance(11, "20", 50, "10:00", "12:00", hours: 2m);

        Assert.Empty(OverlapControl.Evaluate([left, right]));
    }

    [Fact]
    public void Overlap_Partial_ExactDuration()
    {
        var left = Performance(10, "20", 50, "08:00", "10:00", hours: 2m);
        var right = Performance(11, "20", 50, "09:15", "11:00", hours: 1.75m);

        var finding = Assert.Single(OverlapControl.Evaluate([left, right]));
        Assert.Equal(PayrollFindingType.OverlappingPerformances, finding.FindingType);
        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Equal(0.75m, finding.OverlapHours);
    }

    [Fact]
    public void Overlap_Contained_ExactDuration()
    {
        var outer = Performance(10, "20", 50, "08:00", "12:00", hours: 4m);
        var inner = Performance(11, "20", 50, "09:00", "10:00", hours: 1m);

        var finding = Assert.Single(OverlapControl.Evaluate([outer, inner]));
        Assert.Equal(1m, finding.OverlapHours);
    }

    [Fact]
    public void Overlap_MultipleRows_StableNonDuplicate()
    {
        var a = Performance(10, "20", 50, "08:00", "11:00", hours: 3m);
        var b = Performance(11, "20", 50, "09:00", "12:00", hours: 3m);
        var c = Performance(12, "20", 50, "10:00", "13:00", hours: 3m);

        var findings = OverlapControl.Evaluate([a, b, c]);
        Assert.Equal(3, findings.Count);
        Assert.Equal(findings.Select(item => item.FindingKey).Distinct().Count(), findings.Count);
        Assert.Equal(
            findings.OrderBy(item => item.FindingKey, StringComparer.Ordinal).Select(item => item.FindingKey),
            findings.Select(item => item.FindingKey));
    }

    [Fact]
    public void Overlap_AbsenceAndStandby_Excluded()
    {
        var ordinary = Performance(10, "20", 50, "08:00", "12:00", hours: 4m);
        var absence = Performance(11, "20", 50, "09:00", "10:00", hours: 1m, hfdTaakId: 10, isAbsence: true);
        var standby = Performance(12, "20", 50, "09:30", "10:30", hours: 1m, hfdTaakId: 18, isStandby: true);

        Assert.Empty(OverlapControl.Evaluate([ordinary, absence, standby]));
    }

    [Fact]
    public void Engine_CombinesSpecialAndOverlap()
    {
        var project300 = Performance(1, "10", 300, "08:00", "12:00", hours: 4m);
        var left = Performance(10, "10", 50, "13:00", "16:00", hours: 3m);
        var right = Performance(11, "10", 50, "15:00", "17:00", hours: 2m);

        var run = PayrollFindingsEngine.Evaluate(
            [project300, left, right],
            [],
            EmptyDiff(),
            planningQueryCount: 3);

        Assert.Contains(run.Findings, item => item.FindingType == PayrollFindingType.Project300WithoutPlanning);
        Assert.Contains(run.Findings, item => item.FindingType == PayrollFindingType.OverlappingPerformances);
        Assert.Equal(3, run.QueryCount);
        Assert.Contains("Ambiguous", run.PlanningSourceNotes, StringComparison.Ordinal);
    }

    private static Dictionary<string, decimal?> EmptyDiff() =>
        new(StringComparer.Ordinal);

    private static NormalizedPerformanceEntry Performance(
        long id,
        string resourceId,
        int projectNumber,
        string start,
        string end,
        decimal hours,
        int hfdTaakId = 1,
        string? description = null,
        bool isAbsence = false,
        bool isStandby = false)
    {
        var startTime = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        var endTime = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture);
        return new NormalizedPerformanceEntry(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResourceId: resourceId,
            Date: Day,
            Start: new DateTimeOffset(Day.ToDateTime(startTime), TimeSpan.Zero),
            End: new DateTimeOffset(Day.ToDateTime(endTime), TimeSpan.Zero),
            AtlHoursRaw: hours,
            AtlMinutesExact: hours * 60m,
            GrossClockDuration: endTime.ToTimeSpan() - startTime.ToTimeSpan(),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: hfdTaakId,
            ProjectId: $"P{projectNumber}",
            ProjectNumber: projectNumber,
            BonNr: null,
            Description: description,
            Memo: null,
            Postcode: null,
            SortKey: id,
            IsAbsence: isAbsence,
            IsStandby: isStandby);
    }

    private static PayrollPlanningReservation Reservation(
        string resourceId,
        int projectNumber,
        int taskTypeId,
        string from,
        string to) =>
        new(
            IdCalendar: projectNumber * 1000L + resourceId.GetHashCode(StringComparison.Ordinal),
            ResourceId: resourceId,
            Date: Day,
            TimeFrom: TimeOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            TimeTo: TimeOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            TaskTypeId: taskTypeId,
            TaskTypeName: $"type-{taskTypeId}",
            ProjectId: $"P{projectNumber}",
            ProjectNumber: projectNumber,
            HfdTaakId: null,
            Subject: null,
            Classification: PayrollPlanningClassifier.Classify(taskTypeId));
}

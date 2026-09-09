using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Findings;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollMissingTechnicianFindingsTests
{
    private static readonly DateOnly Day = new(2026, 8, 12);
    private static readonly HashSet<string> Included = new(StringComparer.Ordinal) { "A", "B", "C" };

    [Fact]
    public void BothBooked_NoFinding()
    {
        var planning = Plan("A", "B");
        var performances = new[]
        {
            Job(1, "A", "08:05", "15:50"),
            Job(2, "B", "08:10", "15:55"),
        };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], Included);

        Assert.Empty(findings);
    }

    [Fact]
    public void PeerBooked_MissingHasFinding()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], Included);
        var finding = Assert.Single(findings);

        Assert.Equal("B", finding.ResourceId);
        Assert.Equal(PayrollFindingType.MissingPlannedTechnicianPerformance, finding.FindingType);
        Assert.Equal("Mogelijk ontbrekende prestatie", finding.Title);
        Assert.Equal(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer), finding.GpsClassification);
        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Null(finding.SuggestedPayableStart);
    }

    [Fact]
    public void FullDayAbsence_NoFinding()
    {
        var planning = Plan("A", "B");
        var performances = new[]
        {
            Job(1, "A", "08:05", "15:50"),
            Absence(2, "B", hours: 8m),
        };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], Included);

        Assert.Empty(findings);
    }

    [Fact]
    public void ConflictingOtherJob_Contradicted_NoProposal()
    {
        var planning = Plan("A", "B");
        var performances = new[]
        {
            Job(1, "A", "08:05", "15:50"),
            Job(2, "B", "09:00", "12:00", projectId: "OTHER", projectNumber: 999, bon: "X"),
        };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], Included);
        var finding = Assert.Single(findings);

        Assert.Equal(nameof(MissingTechnicianEvidenceClass.ContradictedByExistingPerformance), finding.GpsClassification);
        Assert.Null(finding.SuggestedPayableHours);
        Assert.Contains(2, finding.RelatedPerformanceIds);
    }

    [Fact]
    public void PlanningPeerReliableGps_HighWithProposal()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };
        var gps = MappedDay("B",
        [
            Trip("t1", "08:02", "08:30", km: 12m, drivingMinutes: 20),
            Trip("t2", "15:20", "15:55", km: 11m, drivingMinutes: 18),
        ]);

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [gps], Included);
        var finding = Assert.Single(findings);

        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
        Assert.Equal(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps), finding.GpsClassification);
        Assert.NotNull(finding.SuggestedPayableStart);
        Assert.NotNull(finding.SuggestedPayableEnd);
        Assert.Equal(At("08:00"), finding.SuggestedPayableStart);
        Assert.Equal(At("16:00"), finding.SuggestedPayableEnd);
        Assert.True(finding.SuggestedPayableHours > 0m);
        Assert.Equal("P-JOB", finding.SuggestedProjectId);
        Assert.Equal("BON-1", finding.SuggestedBonNr);
        Assert.Contains("intervalSource=planning", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("suggestedHfdTaakId=14", finding.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("intervalSource=gps", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningPeerNoGps_Review_NotFalseNegative()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };
        var gps = new StandbyGpsDayEvidence(
            "B",
            Day,
            HasVehicleMapping: false,
            MappingAmbiguous: false,
            ObjectId: null,
            RegistrationPlate: null,
            MappingReason: "Unmapped",
            Trips: []);

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [gps], Included);
        var finding = Assert.Single(findings);

        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Equal(nameof(MissingTechnicianEvidenceClass.NoGpsData), finding.GpsClassification);
        Assert.Null(finding.SuggestedPayableStart);
    }

    [Fact]
    public void PlanningGpsNoPeer_NoFinding_ClassifiesPlanningPlusGps()
    {
        var planning = Plan("A", "B");
        var gps = MappedDay("B",
        [
            Trip("t1", "08:10", "09:00", km: 8m, drivingMinutes: 20),
        ]);
        var group = Assert.Single(PlannedWorkGroupBuilder.Build(planning));

        var findings = MissingTechnicianControl.Evaluate([], planning, [gps], Included);

        Assert.Empty(findings);
        Assert.Equal(
            MissingTechnicianEvidenceClass.PlanningPlusGps,
            MissingTechnicianControl.ClassifyWithoutPeer(gps, group));
    }

    [Fact]
    public void GpsClearlyElsewhere_Contradicted_NoProposal()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };
        var gps = MappedDay("B",
        [
            Trip("t1", "01:00", "02:00", km: 20m, drivingMinutes: 40),
            Trip("t2", "22:00", "23:00", km: 15m, drivingMinutes: 30),
        ]);

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [gps], Included);
        var finding = Assert.Single(findings);

        Assert.Equal(nameof(MissingTechnicianEvidenceClass.ContradictedByGps), finding.GpsClassification);
        Assert.Null(finding.SuggestedPayableHours);
    }

    [Fact]
    public void SharedVehicleNoIndependentGps_NotTreatedAsAbsent()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], Included);
        var finding = Assert.Single(findings);

        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.DoesNotContain("afwezig", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sharedVehicleNote", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedPayrollResource_NoFinding()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };
        var included = new HashSet<string>(StringComparer.Ordinal) { "A" };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], included);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExplicitIncludedManualExtra_Eligible()
    {
        var planning = Plan("A", "EXTRA");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };
        var included = new HashSet<string>(StringComparer.Ordinal) { "A", "EXTRA" };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], included);

        Assert.Equal("EXTRA", Assert.Single(findings).ResourceId);
    }

    [Fact]
    public void DuplicatePlanningRows_OneStableFinding()
    {
        var planning = Plan("A", "B").Concat(Plan("A", "B")).ToList();
        var performances = new[] { Job(1, "A", "08:05", "15:50") };

        var findings = MissingTechnicianControl.Evaluate(performances, planning, [], Included);

        Assert.Single(findings);
        Assert.Equal("missing-tech:100:20260812:B", findings[0].FindingKey);
    }

    [Fact]
    public void Engine_IncludesMissingTechnicianFindings()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50", projectNumber: 501) };
        var run = PayrollFindingsEngine.Evaluate(
            performances,
            planning,
            new Dictionary<string, decimal?>(),
            planningQueryCount: 2,
            includedResourceIds: Included);

        Assert.Contains(run.Findings, item => item.FindingType == PayrollFindingType.MissingPlannedTechnicianPerformance);
    }

    [Fact]
    public void LegacyMonthlyStandbyCeiling_Unchanged()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 8, new DateOnly(2026, 9, 1));
        var daily = new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 8, 1)] = 1.2m,
            [new DateOnly(2026, 8, 2)] = 0.4m,
        };
        var result = LegacyStandbyMonthlyCalculator.Calculate(period, "10", daily);
        Assert.Equal(1.6m, result.ExactHours);
        Assert.Equal(2m, result.RoundedHours);
    }

    private static List<PayrollPlanningReservation> Plan(params string[] resourceIds) =>
        resourceIds
            .Select(resourceId => new PayrollPlanningReservation(
                100,
                resourceId,
                Day,
                new TimeOnly(8, 0),
                new TimeOnly(16, 0),
                TaskTypeId: 26,
                TaskTypeName: "Werk",
                ProjectId: "P-JOB",
                ProjectNumber: 501,
                HfdTaakId: 14,
                Subject: "Teamjob",
                Classification: PayrollPlanningClassification.WorkReservation))
            .ToList();

    private static NormalizedPerformanceEntry Job(
        long id,
        string resourceId,
        string start,
        string end,
        string projectId = "P-JOB",
        int projectNumber = 501,
        string bon = "BON-1") =>
        new(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResourceId: resourceId,
            Date: Day,
            Start: At(start),
            End: At(end),
            AtlHoursRaw: (decimal)(At(end) - At(start)).TotalHours,
            AtlMinutesExact: (decimal)(At(end) - At(start)).TotalMinutes,
            GrossClockDuration: At(end) - At(start),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: 1,
            ProjectId: projectId,
            ProjectNumber: projectNumber,
            BonNr: bon,
            Description: "Job",
            Memo: null,
            Postcode: null,
            SortKey: id);

    private static NormalizedPerformanceEntry Absence(long id, string resourceId, decimal hours) =>
        new(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResourceId: resourceId,
            Date: Day,
            Start: At("08:00"),
            End: At("16:00"),
            AtlHoursRaw: hours,
            AtlMinutesExact: hours * 60m,
            GrossClockDuration: TimeSpan.FromHours((double)hours),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: 10,
            ProjectId: null,
            ProjectNumber: null,
            BonNr: null,
            Description: "Verlof",
            Memo: null,
            Postcode: null,
            SortKey: id,
            IsAbsence: true);

    private static StandbyGpsDayEvidence MappedDay(string resourceId, IReadOnlyList<StandbyGpsTripEvidence> trips) =>
        new(
            resourceId,
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ-1",
            RegistrationPlate: "1-ABC-123",
            MappingReason: "Resolved",
            Trips: trips,
            MappingKind: "ObjectId");

    private static StandbyGpsTripEvidence Trip(
        string id,
        string start,
        string end,
        decimal km,
        int drivingMinutes) =>
        new(
            id,
            At(start),
            At(end),
            km,
            drivingMinutes,
            "Home",
            "Site",
            "OBJ-1",
            "1-ABC-123");

    private static DateTimeOffset At(string hhmm)
    {
        var time = TimeOnly.Parse(hhmm, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(Day.ToDateTime(time), TimeSpan.Zero);
    }
}

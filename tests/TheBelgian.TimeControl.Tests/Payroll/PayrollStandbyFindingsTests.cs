using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Findings;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollStandbyFindingsTests
{
    private static readonly DateOnly Day = new(2026, 8, 15);

    [Fact]
    public void PhoneOnly_10Min_NoFinding()
    {
        var standby = Standby(1, "10", "22:00", "22:10", hours: 0.1667m, projectId: "P1");
        var gps = MappedDay("10", trips:
        [
            // Day has GPS, but no meaningful movement during standby → phone-only.
            Trip("t0", "08:00", "08:20", km: 5m, drivingMinutes: 15),
        ]);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);

        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.StandbyPhoneExceeds15Min);
        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.StandbyNoGpsData);
    }

    [Fact]
    public void PhoneOnly_30Min_Exceeds15_SuggestsQuarterHour()
    {
        var standby = Standby(1, "10", "22:00", "22:30", hours: 0.5m, projectId: "P1");
        var gps = MappedDay("10", trips:
        [
            Trip("t1", "08:00", "08:20", km: 4m, drivingMinutes: 12),
        ]);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);
        var finding = Assert.Single(findings, item => item.FindingType == PayrollFindingType.StandbyPhoneExceeds15Min);
        Assert.Equal(0.25m, finding.SuggestedPayableHours);
        Assert.Equal(0.5m, finding.BookedHours);
    }

    [Fact]
    public void PhysicalIntervention_YieldsSuggestedInterval()
    {
        var standby = Standby(1, "10", "20:00", "23:00", hours: 3m, projectId: "P1");
        var gps = MappedDay("10", trips:
        [
            Trip("t1", "20:30", "21:00", km: 12m, drivingMinutes: 25),
            Trip("t2", "21:40", "22:10", km: 11m, drivingMinutes: 20),
        ]);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);
        var mismatch = findings.First(item => item.SuggestedPayableStart is not null);
        Assert.Equal(new TimeOnly(20, 30), TimeOnly.FromDateTime(mismatch.SuggestedPayableStart!.Value.DateTime));
        Assert.Equal(new TimeOnly(22, 10), TimeOnly.FromDateTime(mismatch.SuggestedPayableEnd!.Value.DateTime));
        Assert.True(mismatch.SuggestedPayableHours > 0m);
    }

    [Fact]
    public void BookedStartDiffers_StartMismatch()
    {
        var standby = Standby(1, "10", "19:00", "22:00", hours: 3m, projectId: "P1");
        var gps = MappedDay("10", trips:
        [
            Trip("t1", "20:00", "21:30", km: 20m, drivingMinutes: 40),
        ]);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);
        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.StandbyStartMismatch);
    }

    [Fact]
    public void BookedEndDiffers_EndMismatch()
    {
        var standby = Standby(1, "10", "20:00", "23:30", hours: 3.5m, projectId: "P1");
        var gps = MappedDay("10", trips:
        [
            Trip("t1", "20:00", "21:30", km: 20m, drivingMinutes: 40),
        ]);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);
        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.StandbyEndMismatch);
    }

    [Fact]
    public void NoVehicleMapping_NoGpsData_NotPhoneOnly()
    {
        var standby = Standby(1, "10", "22:00", "22:40", hours: 0.666m, projectId: "P1");
        var gps = new StandbyGpsDayEvidence(
            "10",
            Day,
            HasVehicleMapping: false,
            MappingAmbiguous: false,
            ObjectId: null,
            RegistrationPlate: null,
            MappingReason: "Geen toewijzing",
            Trips: []);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);
        Assert.Contains(findings, item => item.FindingType == PayrollFindingType.StandbyNoGpsData);
        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.StandbyPhoneExceeds15Min);
        Assert.Equal(StandbyGpsClassification.NoGpsData, StandbyControl.ClassifyDay(gps));
    }

    [Fact]
    public void AmbiguousGps_NoAutoTarget()
    {
        var standby = Standby(1, "10", "20:00", "22:00", hours: 2m, projectId: "P1");
        var gps = new StandbyGpsDayEvidence(
            "10",
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: true,
            ObjectId: null,
            RegistrationPlate: null,
            MappingReason: "Ambiguous",
            Trips:
            [
                Trip("t1", "20:10", "21:00", km: 8m, drivingMinutes: 20),
            ]);

        var findings = StandbyControl.Evaluate([standby], [], [gps]);
        var amb = Assert.Single(findings);
        Assert.Equal(PayrollFindingType.StandbyAmbiguousEvidence, amb.FindingType);
        Assert.Null(amb.SuggestedPayableHours);
        Assert.Null(amb.SuggestedPayableStart);
    }

    [Fact]
    public void WrongDossier_StrongPlanningEvidence()
    {
        var standby = Standby(1, "10", "20:00", "22:00", hours: 2m, projectId: "BOOKED");
        var planning = new PayrollPlanningReservation(
            99,
            "10",
            Day,
            new TimeOnly(20, 0),
            new TimeOnly(22, 0),
            TaskTypeId: 26,
            TaskTypeName: "Werk",
            ProjectId: "OTHER",
            ProjectNumber: 501,
            HfdTaakId: null,
            Subject: "Interventie klant",
            Classification: PayrollPlanningClassification.WorkReservation);
        var gps = MappedDay("10", trips:
        [
            Trip("t1", "20:15", "21:45", km: 15m, drivingMinutes: 30),
        ]);

        var findings = StandbyControl.Evaluate([standby], [planning], [gps]);
        var dossier = Assert.Single(findings, item => item.FindingType == PayrollFindingType.StandbyPossibleWrongDossier);
        Assert.Equal("OTHER", dossier.SuggestedProjectId);
        Assert.Equal(PayrollFindingSeverity.High, dossier.Severity);
    }

    [Fact]
    public void CorrectDossier_NoDossierFinding()
    {
        var standby = Standby(1, "10", "20:00", "22:00", hours: 2m, projectId: "SAME");
        var planning = new PayrollPlanningReservation(
            99,
            "10",
            Day,
            new TimeOnly(20, 0),
            new TimeOnly(22, 0),
            TaskTypeId: 26,
            TaskTypeName: "Werk",
            ProjectId: "SAME",
            ProjectNumber: 501,
            HfdTaakId: null,
            Subject: "Interventie",
            Classification: PayrollPlanningClassification.WorkReservation);
        var gps = MappedDay("10", trips:
        [
            Trip("t1", "20:15", "21:45", km: 15m, drivingMinutes: 30),
        ]);

        var findings = StandbyControl.Evaluate([standby], [planning], [gps]);
        Assert.DoesNotContain(findings, item => item.FindingType == PayrollFindingType.StandbyPossibleWrongDossier);
    }

    [Fact]
    public void MultipleSeparateInterventions_NotMerged()
    {
        var first = Standby(1, "10", "02:00", "02:40", hours: 0.666m, projectId: "P1");
        var second = Standby(2, "10", "22:00", "22:40", hours: 0.666m, projectId: "P1");
        var gps = MappedDay("10", trips:
        [
            Trip("t0", "12:00", "12:20", km: 3m, drivingMinutes: 10),
        ]);

        var findings = StandbyControl.Evaluate([first, second], [], [gps]);
        Assert.Equal(2, findings.Count(item => item.FindingType == PayrollFindingType.StandbyPhoneExceeds15Min));
        Assert.Equal(2, findings.Select(item => item.FindingKey).Distinct().Count());
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

    [Fact]
    public void Engine_IncludesStandbyFindings()
    {
        var standby = Standby(1, "10", "22:00", "22:40", hours: 0.666m, projectId: "P1", projectNumber: 501);
        var gps = MappedDay("10", trips:
        [
            Trip("t0", "08:00", "08:20", km: 4m, drivingMinutes: 12),
        ]);
        var run = PayrollFindingsEngine.Evaluate(
            [standby],
            [],
            new Dictionary<string, decimal?>(),
            planningQueryCount: 1,
            [gps],
            gpsQueryCount: 3,
            "gps-notes");
        Assert.Contains(run.Findings, item => item.FindingType == PayrollFindingType.StandbyPhoneExceeds15Min);
        Assert.Equal(4, run.QueryCount);
        Assert.Equal(3, run.GpsQueryCount);
    }

    private static StandbyGpsDayEvidence MappedDay(string resourceId, IReadOnlyList<StandbyGpsTripEvidence> trips) =>
        new(
            resourceId,
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ-1",
            RegistrationPlate: "1-ABC-123",
            MappingReason: "Resolved",
            Trips: trips);

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

    private static NormalizedPerformanceEntry Standby(
        long id,
        string resourceId,
        string start,
        string end,
        decimal hours,
        string? projectId,
        int? projectNumber = 501) =>
        new(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResourceId: resourceId,
            Date: Day,
            Start: At(start),
            End: At(end),
            AtlHoursRaw: hours,
            AtlMinutesExact: hours * 60m,
            GrossClockDuration: At(end) - At(start),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: 23,
            ProjectId: projectId,
            ProjectNumber: projectNumber,
            BonNr: "B-1",
            Description: "Wachtdienst",
            Memo: null,
            Postcode: null,
            SortKey: id,
            IsStandby: true);

    private static DateTimeOffset At(string hhmm)
    {
        var time = TimeOnly.Parse(hhmm, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(Day.ToDateTime(time), TimeSpan.Zero);
    }
}

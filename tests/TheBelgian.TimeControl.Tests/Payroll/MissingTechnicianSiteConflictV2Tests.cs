using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class MissingTechnicianSiteConflictV2Tests
{
    private static readonly DateOnly Day = new(2026, 8, 14);

    [Fact]
    public void ExistingOutsideProposedGpsInterval_DoesNotBlock()
    {
        // Bashi pattern: GPS 07:55–14:46 vs other job 15:15–15:50
        var existing = Perf(281602, "661", "15:15", "15:50", project: 21540, postcode: "2000");
        var overlap = MissingTechnicianSiteConflictAnalyzer.MateriallyOverlapsProposedInterval(
            existing,
            At("07:55"),
            At("14:46"));
        Assert.False(overlap);
    }

    [Fact]
    public void MaterialOverlap_Blocks()
    {
        var existing = Perf(1, "661", "08:00", "16:00", project: 21540, postcode: "2000");
        Assert.True(MissingTechnicianSiteConflictAnalyzer.MateriallyOverlapsProposedInterval(
            existing,
            At("07:55"),
            At("14:46")));
    }

    [Fact]
    public void GpsStopMatchesPlannedPostcode_NotExisting()
    {
        var planned = new JobLocationEvidence(
            "bon:A",
            "P-A",
            40167,
            "A",
            "1785 Merchtem",
            "1785",
            "Merchtem",
            null,
            null,
            250,
            JobLocationSource.PerformancePostcode,
            JobLocationConfidence.Weak);
        var existing = planned with
        {
            Key = "bon:B",
            ProjectId = "P-B",
            ProjectNumber = 21540,
            BonNr = "B",
            Postcode = "9000",
            AddressLabel = "9000 Gent",
            Locality = "Gent",
        };

        var match = MissingTechnicianSiteConflictAnalyzer.ClassifySiteMatch(
            planned,
            existing,
            gpsLatitude: null,
            gpsLongitude: null,
            gpsAddressOrLabel: "Werkzone 1785 Merchtem");

        Assert.Equal(MissingTechnicianSiteMatch.PlannedJobSiteMatch, match);
        Assert.Equal(
            MissingTechnicianConflictClass.PlannedJobSupportedExistingBookingWrong,
            MissingTechnicianSiteConflictAnalyzer.ClassifyConflict(
                Perf(1, "661", "15:15", "15:50", 21540, "9000"),
                match,
                hasCompleteGpsSite: true));
    }

    [Fact]
    public void CompleteGpsWrongSite_DoesNotBecomeHigh()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50", postcode: "1785") };
        var gps = MappedDay("B",
        [
            Trip("t1", "08:02", "08:30"),
            Trip("t2", "15:20", "15:55"),
        ], endAddress: "9000 Gent");

        var locations = new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            ["project:P-JOB"] = new(
                "project:P-JOB", "P-JOB", 501, "BON-1", "1785", "1785", null, null, null, 250,
                JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
        };

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate(performances, planning, [gps], Included, locations));

        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Contains("siteMatch=NeitherMatch", finding.Evidence, StringComparison.Ordinal);
        Assert.Null(finding.SuggestedPayableStart);
    }

    [Fact]
    public void CompleteGpsPlannedSiteMatch_NoConflict_HighReady()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50", postcode: "1785") };
        var gps = MappedDay("B",
        [
            Trip("t1", "08:02", "08:30"),
            Trip("t2", "15:20", "15:55"),
        ], endAddress: "werf 1785 Merchtem");

        var locations = new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            ["project:P-JOB"] = new(
                "project:P-JOB", "P-JOB", 501, "BON-1", "1785 Merchtem", "1785", "Merchtem", null, null, 250,
                JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
            ["bon:BON-1"] = new(
                "bon:BON-1", "P-JOB", 501, "BON-1", "1785 Merchtem", "1785", "Merchtem", null, null, 250,
                JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
        };

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate(performances, planning, [gps], Included, locations));

        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
        Assert.Equal(PayrollFindingType.MissingPlannedTechnicianPerformance, finding.FindingType);
        Assert.Equal(At("08:30"), finding.SuggestedPayableStart);
        Assert.Equal(At("15:20"), finding.SuggestedPayableEnd);
        Assert.Contains("siteMatch=PlannedJobSiteMatch", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("intervalSource=gpsSite", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void BashiStyle_ShortEveningOtherJob_NotMaterialConflict_CanBeHighWithSiteMatch()
    {
        var planning = Plan("401", "661");
        var peer = Job(281607, "401", "08:10", "14:45", projectId: "P-40167", projectNumber: 40167, bon: "B1", postcode: "1785");
        var other = Job(281602, "661", "15:15", "15:50", projectId: "P-21540", projectNumber: 21540, bon: "B2", postcode: "9000");
        var gps = MappedDay("661",
        [
            Trip("in", "07:30", "07:55"),
            Trip("out", "14:46", "15:10"),
        ], endAddress: "1785 site");

        var locations = new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            ["project:P-40167"] = new(
                "project:P-40167", "P-40167", 40167, "B1", "1785", "1785", null, null, null, 250,
                JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
            ["bon:B1"] = new(
                "bon:B1", "P-40167", 40167, "B1", "1785", "1785", null, null, null, 250,
                JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
        };

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate([peer, other], planning, [gps], Included661, locations));

        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
        Assert.DoesNotContain("conflictClass=TimeConflictOnly", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("conflictClass=None", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongDossier_WhenGpsSupportsPlannedAndConflictsWithExisting()
    {
        var planning = Plan("A", "B");
        var peer = Job(1, "A", "08:00", "16:00", postcode: "1785");
        var wrong = Job(2, "B", "07:30", "15:50", projectId: "P-OTHER", projectNumber: 999, bon: "X", postcode: "9000");
        var gps = MappedDay("B",
        [
            Trip("t1", "07:50", "08:10"),
            Trip("t2", "15:40", "16:00"),
        ], endAddress: "1785 Merchtem");

        var locations = new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            ["project:P-JOB"] = new(
                "project:P-JOB", "P-JOB", 501, "BON-1", "1785", "1785", null, null, null, 250,
                JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
            ["project:P-OTHER"] = new(
                "project:P-OTHER", "P-OTHER", 999, "X", "9000", "9000", null, null, null, 250,
                JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
            ["bon:BON-1"] = new(
                "bon:BON-1", "P-JOB", 501, "BON-1", "1785", "1785", null, null, null, 250,
                JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
        };

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate([peer, wrong], planning, [gps], Included, locations));

        Assert.Equal(PayrollFindingType.WrongProjectBooking, finding.FindingType);
        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
        Assert.Contains("verkeerde project/bon", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            nameof(MissingTechnicianConflictClass.PlannedJobSupportedExistingBookingWrong),
            finding.GpsClassification);
    }

    [Fact]
    public void ArrivalOnly_RemainsReview()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50", postcode: "1785") };
        var gps = MappedDay("B", [Trip("t1", "08:02", "08:30")], endAddress: "1785 Merchtem");
        var locations = new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase)
        {
            ["project:P-JOB"] = new(
                "project:P-JOB", "P-JOB", 501, "BON-1", "1785", "1785", null, null, null, 250,
                JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
        };

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate(performances, planning, [gps], Included, locations));
        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Null(finding.SuggestedPayableStart);
    }

    [Fact]
    public void SharedPossible_RemainsNonHigh()
    {
        var planning = Plan("A", "B");
        var performances = new[] { Job(1, "A", "08:05", "15:50") };
        var finding = Assert.Single(MissingTechnicianControl.Evaluate(performances, planning, [], Included));
        Assert.Equal(PayrollFindingSeverity.Review, finding.Severity);
        Assert.Contains("travelMode=SharedTravelPossible", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void BothPossible_AndLocationUnknown_Classified()
    {
        var planned = new JobLocationEvidence(
            "bon:A", "P-A", 1, "A", "1785", "1785", null, null, null, 250,
            JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak);
        var existing = planned with { Key = "bon:B", ProjectNumber = 2, BonNr = "B", Postcode = "1785", AddressLabel = "1785" };

        Assert.Equal(
            MissingTechnicianSiteMatch.BothPossible,
            MissingTechnicianSiteConflictAnalyzer.ClassifySiteMatch(
                planned, existing, null, null, "1785 Merchtem"));

        Assert.Equal(
            MissingTechnicianSiteMatch.LocationUnknown,
            MissingTechnicianSiteConflictAnalyzer.ClassifySiteMatch(
                null, null, null, null, "1785 Merchtem"));
    }

    [Fact]
    public void WrongDossier_ReplacementPlan_IsDeletePlusCreate_NotUpdate()
    {
        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate(
                [
                    Job(1, "A", "08:00", "16:00", postcode: "1785"),
                    Job(2, "B", "07:30", "15:50", projectId: "P-OTHER", projectNumber: 999, bon: "X", postcode: "9000"),
                ],
                Plan("A", "B"),
                [
                    MappedDay("B",
                    [
                        Trip("t1", "07:50", "08:10"),
                        Trip("t2", "15:40", "16:00"),
                    ], endAddress: "1785 Merchtem"),
                ],
                Included,
                new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase)
                {
                    ["project:P-JOB"] = new(
                        "project:P-JOB", "P-JOB", 501, "BON-1", "1785", "1785", null, null, null, 250,
                        JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
                    ["project:P-OTHER"] = new(
                        "project:P-OTHER", "P-OTHER", 999, "X", "9000", "9000", null, null, null, 250,
                        JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak),
                    ["bon:BON-1"] = new(
                        "bon:BON-1", "P-JOB", 501, "BON-1", "1785", "1785", null, null, null, 250,
                        JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
                }));

        Assert.Equal(PayrollFindingType.WrongProjectBooking, finding.FindingType);
        Assert.Contains("verwijderen", finding.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aanmaken", finding.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", finding.SuggestedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingBookedSiteMatch_ConflictClass_ExistingSupported()
    {
        var planned = new JobLocationEvidence(
            "bon:A", "P-A", 1, "A", "1785", "1785", null, null, null, 250,
            JobLocationSource.PerformancePostcode, JobLocationConfidence.Weak);
        var existing = planned with
        {
            Key = "bon:B",
            ProjectId = "P-B",
            ProjectNumber = 2,
            BonNr = "B",
            Postcode = "9000",
            AddressLabel = "9000",
        };

        var match = MissingTechnicianSiteConflictAnalyzer.ClassifySiteMatch(
            planned, existing, null, null, "9000 Gent");
        Assert.Equal(MissingTechnicianSiteMatch.ExistingBookedJobSiteMatch, match);
        Assert.Equal(
            MissingTechnicianConflictClass.ExistingPerformanceSupported,
            MissingTechnicianSiteConflictAnalyzer.ClassifyConflict(
                Perf(1, "B", "08:00", "16:00", 2, "9000"),
                match,
                hasCompleteGpsSite: true));
    }

    private static readonly HashSet<string> Included = new(StringComparer.Ordinal) { "A", "B" };
    private static readonly HashSet<string> Included661 = new(StringComparer.Ordinal) { "401", "661" };

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
                ProjectId: resourceId is "401" or "661" ? "P-40167" : "P-JOB",
                ProjectNumber: resourceId is "401" or "661" ? 40167 : 501,
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
        string bon = "BON-1",
        string? postcode = null) =>
        Perf(id, resourceId, start, end, projectNumber, postcode, projectId, bon);

    private static NormalizedPerformanceEntry Perf(
        long id,
        string resourceId,
        string start,
        string end,
        int project,
        string? postcode,
        string? projectId = null,
        string? bon = null) =>
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
            HfdTaakId: 14,
            ProjectId: projectId ?? $"P{project}",
            ProjectNumber: project,
            BonNr: bon ?? "BON",
            Description: "Job",
            Memo: null,
            Postcode: postcode,
            SortKey: id);

    private static StandbyGpsDayEvidence MappedDay(
        string resourceId,
        IReadOnlyList<StandbyGpsTripEvidence> trips,
        string endAddress = "Site") =>
        new(
            resourceId,
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ-1",
            RegistrationPlate: "1-ABC-123",
            MappingReason: "Resolved",
            Trips: trips.Select(t => t with { EndAddress = endAddress, StartAddress = "Home" }).ToList(),
            MappingKind: "ObjectId");

    private static StandbyGpsTripEvidence Trip(string id, string start, string end) =>
        new(id, At(start), At(end), 10m, 20, "Home", "Site", "OBJ-1", "1-ABC-123");

    private static DateTimeOffset At(string hhmm) =>
        new(Day.ToDateTime(TimeOnly.Parse(hhmm, System.Globalization.CultureInfo.InvariantCulture)), TimeSpan.Zero);
}

using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class MissingTechnicianWorkdayContinuityV3Tests
{
    private static readonly DateOnly Day = new(2026, 8, 14);
    private static readonly HashSet<string> Included = new(StringComparer.Ordinal) { "401", "661" };
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    [Fact]
    public void SiteToHqMaterialPickup_SameSiteReturn_ContinuousSupported()
    {
        var excursions = MissingTechnicianWorkdayContinuity.DetectExcursions(
            At("07:55"),
            At("14:46"),
            BashiGpsDay(),
            "2630",
            [],
            PseudoGroup());

        var hq = Assert.Single(excursions);
        Assert.Equal(MissingTechnicianExcursionClass.WorkHqVisit, hq.Classification);
        Assert.Equal(At("12:22"), hq.DepartSite);
        Assert.Equal(At("13:42"), hq.ReturnSite);

        var continuity = MissingTechnicianWorkdayContinuity.ClassifyContinuity(
            excursions,
            MissingTechnicianOperationalSiteRelation.SameOperationalSiteProven,
            At("07:55"),
            At("14:46"));
        Assert.Equal(MissingTechnicianWorkContinuity.ContinuousSupported, continuity);
        Assert.True(MissingTechnicianWorkdayContinuity.AllowsContinuousProposal(continuity));
    }

    [Fact]
    public void SiteToSupplier_SameSiteReturn_ContinuousSupportedOrPlausible()
    {
        var gps = MappedDay("661",
        [
            Leg("in", "07:30", "07:55", "Lebbeke", "Ingberthoeveweg 21, 2630 Aartselaar"),
            Leg("toSup", "12:00", "12:20", "Ingberthoeveweg 1, 2630 Aartselaar", "Industrielaan 5, 2000 Antwerpen"),
            Leg("back", "12:40", "13:00", "Industrielaan 5, 2000 Antwerpen", "Ingberthoeveweg 1, 2630 Aartselaar"),
            Leg("out", "14:46", "15:10", "Ingberthoeveweg 1, 2630 Aartselaar", "Lier"),
        ]);

        var excursions = MissingTechnicianWorkdayContinuity.DetectExcursions(
            At("07:55"), At("14:46"), gps, "2630", [], PseudoGroup());

        Assert.NotEmpty(excursions);
        // No supplier catalog → UNKNOWN destination but return-to-site keeps continuity plausible.
        var continuity = MissingTechnicianWorkdayContinuity.ClassifyContinuity(
            excursions,
            MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely,
            At("07:55"),
            At("14:46"));
        Assert.Equal(MissingTechnicianWorkContinuity.ContinuousPlausible, continuity);
    }

    [Fact]
    public void SiteToLunchShop_SameSiteReturn_DoesNotForceSplit()
    {
        var gps = MappedDay("661",
        [
            Leg("in", "07:50", "08:00", "Home", "Werfstraat 1, 2630 Aartselaar"),
            Leg("lunch", "12:05", "12:15", "Werfstraat 1, 2630 Aartselaar", "Bakkerij Broodje, 2630 Aartselaar"),
            Leg("back", "12:25", "12:35", "Bakkerij Broodje, 2630 Aartselaar", "Werfstraat 1, 2630 Aartselaar"),
            Leg("out", "15:00", "15:20", "Werfstraat 1, 2630 Aartselaar", "Home"),
        ]);

        // Same postcode lunch: DetectExcursions skips ends still in 2630 — treat as meal via ClassifyExcursion API.
        var cls = MissingTechnicianWorkdayContinuity.ClassifyExcursion(
            At("12:05"),
            At("12:15"),
            At("12:25"),
            At("12:35"),
            "Bakkerij Broodje Merchtem",
            null,
            null,
            [],
            PseudoGroup());
        Assert.Equal(MissingTechnicianExcursionClass.MealBreak, cls);

        var continuity = MissingTechnicianWorkdayContinuity.ClassifyContinuity(
            [
                new MissingTechnicianExcursion(
                    At("12:05"), At("12:15"), At("12:25"), At("12:35"),
                    "Bakkerij Broodje", null, null, MissingTechnicianExcursionClass.MealBreak, 30),
            ],
            MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely,
            At("08:00"),
            At("15:00"));
        Assert.Equal(MissingTechnicianWorkContinuity.ContinuousPlausible, continuity);
        Assert.False(continuity == MissingTechnicianWorkContinuity.SplitRequired);
    }

    [Fact]
    public void LunchGpsExcursion_DoesNotReduceSuggestedHours_CanonicalPauseNoteOnly()
    {
        var planning = Plan();
        var peer = PeerJob();
        var gps = MappedDay("661",
        [
            Leg("in", "07:30", "07:55", "Lebbeke", "Ingberthoeveweg 21, 2630 Aartselaar"),
            Leg("hq", "12:22", "12:48", "Kontichsesteenweg 22, 2630 Aartselaar", "Slozenstraat 86, 1861 Meise"),
            Leg("back", "13:03", "13:42", "Slozenstraat 86, 1861 Meise", "Ingberthoeveweg 1, 2630 Aartselaar"),
            Leg("out", "14:46", "15:17", "Ingberthoeveweg 1, 2630 Aartselaar", "Lier"),
        ]);

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate([peer], planning, [gps], Included, SiteMap()));

        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
        Assert.Equal(At("07:55"), finding.SuggestedPayableStart);
        Assert.Equal(At("14:46"), finding.SuggestedPayableEnd);
        Assert.Equal(6.85m, finding.SuggestedPayableHours);
        Assert.Contains("pauseNote=", finding.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("GPS-aftrek van voorsteluren", finding.Evidence, StringComparison.OrdinalIgnoreCase);
        // Gross hours preserved — no geofence subtraction of HQ gap.
        Assert.Contains("workContinuity=CONTINUOUS_SUPPORTED", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void SiteToOtherCustomerJob_SplitRequired()
    {
        var other = Job(99, "661", "12:30", "13:20", projectId: "P-OTHER", projectNumber: 999, bon: "X", postcode: "2000");
        var excursions = MissingTechnicianWorkdayContinuity.DetectExcursions(
            At("07:55"),
            At("14:46"),
            MappedDay("661",
            [
                Leg("in", "07:30", "07:55", "Home", "Ingberthoeveweg 21, 2630 Aartselaar"),
                Leg("away", "12:00", "12:25", "Ingberthoeveweg 1, 2630 Aartselaar", "Klantstraat 1, 2000 Antwerpen"),
                Leg("back", "13:30", "13:50", "Klantstraat 1, 2000 Antwerpen", "Ingberthoeveweg 1, 2630 Aartselaar"),
                Leg("out", "14:46", "15:10", "Ingberthoeveweg 1, 2630 Aartselaar", "Home"),
            ]),
            "2630",
            [other],
            PseudoGroup());

        Assert.Contains(excursions, e => e.Classification == MissingTechnicianExcursionClass.OtherJob);
        Assert.Equal(
            MissingTechnicianWorkContinuity.SplitRequired,
            MissingTechnicianWorkdayContinuity.ClassifyContinuity(
                excursions,
                MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely,
                At("07:55"),
                At("14:46")));
    }

    [Fact]
    public void PersonalOrNonwork_ForcesSplit()
    {
        var continuity = MissingTechnicianWorkdayContinuity.ClassifyContinuity(
            [
                new MissingTechnicianExcursion(
                    At("11:00"), At("11:30"), At("14:00"), At("14:30"),
                    "Home", null, null, MissingTechnicianExcursionClass.PersonalOrNonwork, 210),
            ],
            MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely,
            At("08:00"),
            At("16:00"));
        Assert.Equal(MissingTechnicianWorkContinuity.SplitRequired, continuity);
    }

    [Fact]
    public void DifferentEntrancesSamePostcode_NotFalseMismatch()
    {
        var relation = MissingTechnicianWorkdayContinuity.ClassifyOperationalSite(
            "2630",
            "Ingberthoeveweg",
            new JobLocationEvidence(
                "bon:1", "40167", 40167, "26501760", "Dijkstraat 8, 2630 Aartselaar", "2630", "Aartselaar",
                null, null, 250, JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
            "Ingberthoeveweg 21, 2630 Aartselaar",
            peerGps: null,
            peer: null);

        Assert.Equal(MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely, relation);
        Assert.NotEqual(MissingTechnicianOperationalSiteRelation.DifferentSite, relation);
    }

    [Fact]
    public void PeerGpsSupportsOperationalSite_StrongerEvidence()
    {
        var peer = PeerJob();
        var peerGps = MappedDay("401",
        [
            Leg("p1", "08:00", "08:15", "Home", "Ingberthoeveweg 1, 2630 Aartselaar"),
            Leg("p2", "14:40", "15:00", "Ingberthoeveweg 1, 2630 Aartselaar", "Home"),
        ]);

        var relation = MissingTechnicianWorkdayContinuity.ClassifyOperationalSite(
            "2630",
            "Ingberthoeveweg",
            new JobLocationEvidence(
                "bon:1", "40167", 40167, "26501760", "Dijkstraat 8", "2630", "Aartselaar",
                null, null, 250, JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
            "Ingberthoeveweg 21, 2630 Aartselaar",
            peerGps,
            peer);

        Assert.Equal(MissingTechnicianOperationalSiteRelation.SameOperationalSiteProven, relation);
    }

    [Fact]
    public void ContinuousWorkAllocationUncertain_HoursPreserved()
    {
        var planning = Plan();
        var peer = PeerJob();
        var gps = BashiGpsDay();

        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate([peer], planning, [gps], Included, SiteMap()));

        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
        Assert.Equal(6.85m, finding.SuggestedPayableHours);
        Assert.Contains("allocationReview=true", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("excursion=WORK_HQ_VISIT", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("workContinuity=CONTINUOUS_SUPPORTED", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void IsTheBelgianHq_MatchesSlozenstraatMeise()
    {
        Assert.True(MissingTechnicianWorkdayContinuity.IsTheBelgianHq(
            "Slozenstraat 86, 1861 Meise, België",
            50.98441m,
            4.30067m));
        Assert.False(MissingTechnicianWorkdayContinuity.IsTheBelgianHq(
            "Ingberthoeveweg 1, 2630 Aartselaar",
            51.14914m,
            4.39431m));
    }

    [Fact]
    public void SameProposalFields_RemainHighReady_ActionSemanticsUnchanged()
    {
        // Existing ReadyForApproval 07:55–14:46 stays aligned with V3 continuous HQ visit.
        var finding = Assert.Single(
            MissingTechnicianControl.Evaluate([PeerJob()], Plan(), [BashiGpsDay()], Included, SiteMap()));

        Assert.Equal(At("07:55"), finding.SuggestedPayableStart);
        Assert.Equal(At("14:46"), finding.SuggestedPayableEnd);
        Assert.Equal("40167", finding.SuggestedProjectId);
        Assert.Equal("26501760", finding.SuggestedBonNr);
        Assert.Contains("suggestedHfdTaakId=9", finding.Evidence, StringComparison.Ordinal);
    }

    private static StandbyGpsDayEvidence BashiGpsDay() =>
        MappedDay("661",
        [
            Leg("in", "07:15", "07:55", "Fabrieksstraat 41, 9280 Lebbeke", "Ingberthoeveweg 21, 2630 Aartselaar"),
            Leg("micro", "08:15", "08:16", "Ingberthoeveweg 21, 2630 Aartselaar", "Ingberthoeveweg 1, 2630 Aartselaar"),
            Leg("near", "12:02", "12:09", "Ingberthoeveweg 1, 2630 Aartselaar", "Kontichsesteenweg 22, 2630 Aartselaar"),
            Leg("hq", "12:22", "12:48", "Kontichsesteenweg 22, 2630 Aartselaar", "Slozenstraat 86, 1861 Meise"),
            Leg("back", "13:03", "13:42", "Slozenstraat 86, 1861 Meise", "Ingberthoeveweg 1, 2630 Aartselaar"),
            Leg("out", "14:46", "15:17", "Ingberthoeveweg 1, 2630 Aartselaar", "Antwerpsesteenweg 469, 2500 Lier"),
        ]);

    private static Dictionary<string, JobLocationEvidence> SiteMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["project:40167"] = new(
            "project:40167", "40167", 40167, "26501760", "Dijkstraat 8, 2630 Aartselaar", "2630", "Aartselaar",
            null, null, 250, JobLocationSource.ProjectSiteAddress, JobLocationConfidence.Weak),
        ["bon:26501760"] = new(
            "bon:26501760", "40167", 40167, "26501760", "Dijkstraat 8, 2630 Aartselaar", "2630", "Aartselaar",
            null, null, 250, JobLocationSource.BonInterventionAddress, JobLocationConfidence.Weak),
    };

    private static PlannedWorkGroup PseudoGroup()
    {
        var reservations = Plan();
        var groups = PlannedWorkGroupBuilder.Build(reservations);
        Assert.NotEmpty(groups);
        return groups[0];
    }

    private static List<PayrollPlanningReservation> Plan() =>
    [
        new(154651, "661", Day, new TimeOnly(8, 0), new TimeOnly(16, 30), 15, "Werk", "40167", 40167, 9, "Team",
            PayrollPlanningClassification.WorkReservation),
        new(154651, "401", Day, new TimeOnly(8, 0), new TimeOnly(16, 30), 15, "Werk", "40167", 40167, 9, "Team",
            PayrollPlanningClassification.WorkReservation),
    ];

    private static NormalizedPerformanceEntry PeerJob() =>
        Job(281607, "401", "08:10", "14:45", projectId: "40167", projectNumber: 40167, bon: "26501760", postcode: "2630");

    private static NormalizedPerformanceEntry Job(
        long id,
        string resourceId,
        string start,
        string end,
        string? projectId = "40167",
        int? projectNumber = 40167,
        string? bon = "26501760",
        string? postcode = "2630")
    {
        var s = At(start);
        var e = At(end);
        return new NormalizedPerformanceEntry(
            id,
            id.ToString(CultureInfo.InvariantCulture),
            resourceId,
            Day,
            s,
            e,
            (decimal)(e - s).TotalHours,
            0m,
            e - s,
            new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            null,
            9,
            projectId,
            projectNumber,
            bon,
            "job",
            null,
            postcode,
            id);
    }

    private static StandbyGpsDayEvidence MappedDay(string resourceId, StandbyGpsTripEvidence[] trips) =>
        new(
            resourceId,
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "obj-" + resourceId,
            RegistrationPlate: "1-TEST-001",
            MappingReason: "test",
            Trips: trips,
            MappingKind: "DaySpecificDriverEvidence");

    private static StandbyGpsTripEvidence Leg(
        string id,
        string start,
        string end,
        string startAddr,
        string endAddr) =>
        new(
            id,
            At(start),
            At(end),
            1m,
            Math.Max(1, (int)(At(end) - At(start)).TotalMinutes),
            startAddr,
            endAddr,
            "obj",
            "1-TEST-001",
            null,
            null,
            null,
            null);

    private static DateTimeOffset At(string hhmm)
    {
        var parts = hhmm.Split(':');
        return new DateTimeOffset(
            Day.ToDateTime(new TimeOnly(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture))),
            Offset);
    }
}

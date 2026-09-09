using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class OverlapIntelligenceTests
{
    private static readonly DateOnly Day = new(2026, 8, 12);

    [Fact]
    public void ExactDuplicate_SameBon_ProposesDeleteLaterRow()
    {
        var a = Perf(10, "08:00", "12:00", project: 50, bon: "123", desc: "Werk");
        var b = Perf(20, "08:00", "12:00", project: 50, bon: "123", desc: null);

        var analysis = OverlapIntelligence.Analyze(a, b);

        Assert.Equal(OverlapKind.ExactDuplicate, analysis.Kind);
        Assert.Equal(OverlapRecommendationAction.DeleteDuplicate, analysis.Recommendation);
        Assert.Equal(20, analysis.TargetPerformanceId);
        Assert.Equal(OverlapConfidence.High, analysis.Confidence);
        Assert.Contains("zelfde boeking", analysis.AdviceNl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactDuplicate_DifferentCreationTime_PrefersHigherId()
    {
        var earlier = Perf(5, "09:00", "11:00", project: 50, bon: "BON-1", desc: "A");
        var later = Perf(99, "09:00", "11:00", project: 50, bon: "BON-1", desc: "A");

        Assert.Equal(99, OverlapIntelligence.PreferDuplicateDeleteTarget(earlier, later));
    }

    [Fact]
    public void PartialSameJob_NoGps_Uncertain()
    {
        var a = Perf(1, "08:00", "12:00", project: 50, bon: "123");
        var b = Perf(2, "11:30", "14:00", project: 50, bon: "123");

        var analysis = OverlapIntelligence.Analyze(a, b);

        Assert.Equal(OverlapKind.PartialSameJob, analysis.Kind);
        Assert.Equal(0.5m, analysis.OverlapHours);
        Assert.Equal(OverlapRecommendationAction.Uncertain, analysis.Recommendation);
    }

    [Fact]
    public void PartialDifferentJobs_Classified()
    {
        var a = Perf(1, "08:00", "12:00", project: 50, bon: "AAA");
        var b = Perf(2, "11:30", "15:00", project: 60, bon: "BBB");

        var analysis = OverlapIntelligence.Analyze(a, b);

        Assert.Equal(OverlapKind.PartialDifferentJobs, analysis.Kind);
    }

    [Fact]
    public void GpsSupportsB_BoundaryTrim()
    {
        var a = Perf(1, "08:00", "12:00", project: 50, bon: "A1");
        var b = Perf(2, "11:30", "15:00", project: 60, bon: "B1");
        var gps = new StandbyGpsDayEvidence(
            "R1",
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ-1",
            RegistrationPlate: "1-ABC-123",
            MappingReason: "Mapped",
            Trips:
            [
                new StandbyGpsTripEvidence(
                    "t1",
                    At("08:05"),
                    At("11:44"),
                    10m,
                    30,
                    "A",
                    "SiteA",
                    "OBJ-1",
                    "1-ABC-123"),
                new StandbyGpsTripEvidence(
                    "t2",
                    At("11:50"),
                    At("12:18"),
                    8m,
                    20,
                    "SiteA",
                    "SiteB",
                    "OBJ-1",
                    "1-ABC-123"),
            ]);

        var analysis = OverlapIntelligence.Analyze(a, b, gps);

        Assert.Equal(OverlapRecommendationAction.AdjustBoundary, analysis.Recommendation);
        Assert.Equal(2, analysis.TargetPerformanceId);
        Assert.Equal(At("12:18"), analysis.ProposedStart);
        Assert.Equal(OverlapConfidence.High, analysis.Confidence);
        Assert.Equal(OverlapGpsSupport.SupportsB, analysis.GpsSupport);
    }

    [Fact]
    public void TravelWorkOverlap_Classified()
    {
        var work = Perf(1, "08:00", "12:00", project: 50, bon: "1", hfd: 14);
        var travel = Perf(2, "11:30", "12:30", project: 50, bon: "1", hfd: 5, travel: true);

        var analysis = OverlapIntelligence.Analyze(work, travel);

        Assert.Equal(OverlapKind.TravelWorkOverlap, analysis.Kind);
        Assert.Equal(OverlapRecommendationAction.Uncertain, analysis.Recommendation);
    }

    [Fact]
    public void SubMinuteOverlap_IgnoredByControl()
    {
        var a = Perf(1, "08:00", "10:00", project: 50);
        var b = Perf(2, "09:59", "11:00", project: 50);
        // Force ~1 minute by adjusting — still above minimum; create true sub-minute via Analyze window.
        var tiny = Perf(3, "10:00", "10:00", project: 50); // invalid end==start filtered by control
        Assert.Empty(OverlapControl.Evaluate([a, tiny]));

        var almostTouch = a with
        {
            End = At("10:00"),
        };
        var startJustAfter = b with
        {
            Start = At("10:00").AddSeconds(30),
            End = At("11:00"),
        };
        Assert.Empty(OverlapControl.Evaluate([almostTouch, startJustAfter]));
    }

    [Fact]
    public void PayrollImpact_DeleteRespectsOverlapCorrection()
    {
        var a = Perf(1, "08:00", "12:00", project: 50, hours: 4m);
        var b = Perf(2, "11:00", "14:00", project: 50, hours: 3m);
        var preview = OverlapPayrollImpactCalculator.ForDelete([a, b], deletePerformanceId: 2);

        Assert.True(preview.OverlapCorrectionBefore > 0m);
        Assert.Equal(0m, preview.OverlapCorrectionAfter);
        // Net is NOT simply -3h ATL; overlap correction disappears.
        Assert.NotEqual(-3m, preview.NetPayrollEffectHours);
        Assert.Contains("overlapcorrectie", preview.SummaryNl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Control_ExactDuplicate_HighWithKind()
    {
        var a = Perf(10, "08:00", "12:00", project: 50, bon: "X", desc: "ok");
        var b = Perf(11, "08:00", "12:00", project: 50, bon: "X", desc: null);
        var finding = Assert.Single(OverlapControl.Evaluate([a, b]));
        Assert.Equal(nameof(OverlapKind.ExactDuplicate), finding.GpsClassification);
        Assert.Equal(PayrollFindingSeverity.High, finding.Severity);
    }

    private static NormalizedPerformanceEntry Perf(
        long id,
        string start,
        string end,
        int project,
        string? bon = null,
        string? desc = null,
        int hfd = 14,
        bool travel = false,
        decimal? hours = null)
    {
        var s = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        var e = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture);
        var h = hours ?? (decimal)(e.ToTimeSpan() - s.ToTimeSpan()).TotalHours;
        return new NormalizedPerformanceEntry(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResourceId: "R1",
            Date: Day,
            Start: new DateTimeOffset(Day.ToDateTime(s), TimeSpan.Zero),
            End: new DateTimeOffset(Day.ToDateTime(e), TimeSpan.Zero),
            AtlHoursRaw: h,
            AtlMinutesExact: h * 60m,
            GrossClockDuration: e.ToTimeSpan() - s.ToTimeSpan(),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: hfd,
            ProjectId: $"P{project}",
            ProjectNumber: project,
            BonNr: bon,
            Description: desc,
            Memo: null,
            Postcode: null,
            SortKey: id,
            IsTravel: travel);
    }

    private static DateTimeOffset At(string hhmm) =>
        new(Day.ToDateTime(TimeOnly.Parse(hhmm, System.Globalization.CultureInfo.InvariantCulture)), TimeSpan.Zero);
}

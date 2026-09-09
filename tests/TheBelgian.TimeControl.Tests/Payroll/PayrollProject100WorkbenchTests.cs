using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollProject100WorkbenchTests
{
    private static readonly DateOnly Thursday = new(2026, 8, 27);
    private static readonly DateOnly Friday = new(2026, 8, 28);
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    [Fact]
    public void TheoreticalDay_MondayToThursday_Is8h_Friday_Is7h()
    {
        var thu = PayrollProject100WorkbenchBuilder.BuildDayTotals(
            Thursday,
            [Perf("10", 1, "08:00", "16:00", 8m, 501, Thursday)],
            []);
        Assert.Equal(8m, thu.TheoreticalDayHours);

        var fri = PayrollProject100WorkbenchBuilder.BuildDayTotals(
            Friday,
            [Perf("10", 1, "08:00", "15:00", 7m, 501, Friday)],
            []);
        Assert.Equal(7m, fri.TheoreticalDayHours);
    }

    [Fact]
    public void TrainingBelowNorm_ShowsZeroTrainingOvertime()
    {
        var training = Perf("10", 100, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21);
        var other = Perf("10", 50, "13:00", "17:00", 4m, 501, Thursday);
        var totals = PayrollProject100WorkbenchBuilder.BuildDayTotals(Thursday, [other, training], [training]);

        Assert.Equal(4m, totals.OtherWorkHours);
        Assert.Equal(3m, totals.TrainingHours);
        Assert.Equal(0m, totals.PotentialOrdinaryOvertimeBeforeTrainingRule);
        Assert.Equal(0m, totals.PayableOvertimeAttributableToTraining);
    }

    [Fact]
    public void TrainingExactlyReachesNorm_NoOvertime()
    {
        var training = Perf("10", 100, "08:00", "12:00", 4m, 100, Thursday, hfdTaakId: 21);
        var other = Perf("10", 50, "13:00", "17:00", 4m, 501, Thursday);
        var totals = PayrollProject100WorkbenchBuilder.BuildDayTotals(Thursday, [other, training], [training]);

        Assert.Equal(8m, totals.OtherWorkHours + totals.TrainingHours);
        Assert.Equal(0m, totals.PotentialOrdinaryOvertimeBeforeTrainingRule);
        Assert.Equal(0m, totals.PayableOvertimeAttributableToTraining);
    }

    [Fact]
    public void TrainingWouldExceedNorm_TrainingCreatesNoPayableOvertime()
    {
        var training = Perf("10", 100, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21);
        var other = Perf("10", 50, "07:00", "13:00", 6m, 501, Thursday);
        var totals = PayrollProject100WorkbenchBuilder.BuildDayTotals(Thursday, [other, training], [training]);

        Assert.Equal(6m, totals.OtherWorkHours);
        Assert.Equal(3m, totals.TrainingHours);
        Assert.Equal(1m, totals.PotentialOrdinaryOvertimeBeforeTrainingRule);
        Assert.Equal(0m, totals.PayableOvertimeAttributableToTraining);
    }

    [Fact]
    public void MixedWorkAndTraining_PreservesOtherWorkHours()
    {
        var training = Perf("10", 100, "09:00", "11:00", 2m, 100, Friday, hfdTaakId: 21);
        var travel = Perf("10", 51, "07:30", "08:30", 1m, 501, Friday, description: "verplaatsing");
        var work = Perf("10", 52, "13:00", "16:00", 3m, 501, Friday);
        var totals = PayrollProject100WorkbenchBuilder.BuildDayTotals(Friday, [travel, training, work], [training]);

        Assert.Equal(4m, totals.OtherWorkHours);
        Assert.Equal(2m, totals.TrainingHours);
        Assert.Equal(7m, totals.TheoreticalDayHours);
        Assert.Equal(0m, totals.PayableOvertimeAttributableToTraining);
    }

    [Fact]
    public void PlanningEvidence_PrefillsAdjustSuggestion()
    {
        var admin = AdminCase(100, 3.5m, Thursday);
        var day = new[] { Perf("10", 100, "08:30", "12:00", 3.5m, 100, Thursday, hfdTaakId: 21) };
        var planning = new[] { Reservation(9, 100, "09:00", "12:00", "Toolbox", Thursday) };

        var detail = PayrollProject100WorkbenchBuilder.BuildDetail(admin, day, planning, gps: null);
        Assert.NotNull(detail.PlanningComparison);
        var cmp = detail.PlanningComparison!;
        Assert.True(cmp.HasMatchingPlanning);
        Assert.Equal("+30 min", cmp.DifferenceSummary);
        Assert.Equal(new TimeOnly(9, 0), cmp.SuggestedAdjustStart);
        Assert.Equal(new TimeOnly(12, 0), cmp.SuggestedAdjustEnd);
        Assert.Contains("Opleiding geboekt", cmp.TopSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlanning_ShowsGeenPassendePlanning()
    {
        var admin = AdminCase(100, 3m, Thursday);
        var day = new[] { Perf("10", 100, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21) };
        var detail = PayrollProject100WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        Assert.False(detail.PlanningComparison!.HasMatchingPlanning);
        Assert.Contains("Geen passende planning", detail.PlanningComparison.TopSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerDuration_MajorityEarlierStart_SummarizesAndSuggests()
    {
        var admin = AdminCase(100, 3.5m, Thursday);
        var day = new[] { Perf("10", 100, "08:30", "12:00", 3.5m, 100, Thursday, hfdTaakId: 21) };
        var peers = new[]
        {
            Perf("11", 201, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21),
            Perf("12", 202, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21),
            Perf("13", 203, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21),
        };

        var detail = PayrollProject100WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            peerSessionPerformances: peers);

        Assert.NotNull(detail.PeerEvidence);
        var peer = detail.PeerEvidence!;
        Assert.Equal(3, peer.PeerCount);
        Assert.Equal("09:00–12:00", peer.TypicalIntervalSummary);
        Assert.Equal(3, peer.TypicalCount);
        Assert.Contains("3 collega's boekten 09:00–12:00", peer.AdminSummary, StringComparison.Ordinal);
        Assert.Contains("vroeger", peer.AdminSummary, StringComparison.Ordinal);
        Assert.Contains("30 min", peer.AdminSummary, StringComparison.Ordinal);
        Assert.Equal(new TimeOnly(9, 0), peer.SuggestedAdjustStart);
        Assert.Equal(new TimeOnly(12, 0), peer.SuggestedAdjustEnd);
    }

    [Fact]
    public void ConflictingPeerEvidence_DoesNotAutoSuggest()
    {
        var admin = AdminCase(100, 3m, Thursday);
        var day = new[] { Perf("10", 100, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21) };
        var peers = new[]
        {
            Perf("11", 201, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21),
            Perf("12", 202, "08:30", "12:00", 3.5m, 100, Thursday, hfdTaakId: 21),
        };

        var peer = PayrollProject100WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            peerSessionPerformances: peers).PeerEvidence!;

        Assert.Equal(2, peer.PeerCount);
        Assert.Null(peer.SuggestedAdjustStart);
    }

    [Fact]
    public void PlanningPlusPeerAgreement_KeepsPlanningSuggestion()
    {
        var admin = AdminCase(100, 3.5m, Thursday);
        var day = new[] { Perf("10", 100, "08:30", "12:00", 3.5m, 100, Thursday, hfdTaakId: 21) };
        var planning = new[] { Reservation(9, 100, "09:00", "12:00", "Toolbox", Thursday) };
        var peers = new[]
        {
            Perf("11", 201, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21),
            Perf("12", 202, "09:00", "12:00", 3m, 100, Thursday, hfdTaakId: 21),
        };

        var detail = PayrollProject100WorkbenchBuilder.BuildDetail(
            admin,
            day,
            planning,
            gps: null,
            peerSessionPerformances: peers);

        Assert.Equal(new TimeOnly(9, 0), detail.PlanningComparison!.SuggestedAdjustStart);
        Assert.NotNull(detail.PeerEvidence);
        Assert.Contains("collega's", detail.FocusedContext!.ContextSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleProject100Rows_RemainSeparateInBookedRows()
    {
        var admin = AdminCaseMulti([101, 102], 5m, Thursday);
        var day = new[]
        {
            Perf("10", 101, "09:00", "11:00", 2m, 100, Thursday, hfdTaakId: 21),
            Perf("10", 102, "13:00", "16:00", 3m, 100, Thursday, hfdTaakId: 21),
        };

        var detail = PayrollProject100WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        Assert.Equal(2, detail.BookedRows.Count);
        Assert.Equal(2, detail.CorrectionTargets.Count(item =>
            item.CorrectionCapability == PayrollProject100CorrectionCapability.SupportedDelete));
        Assert.Contains(detail.BookedRows, item => item.PerformanceId == 101);
        Assert.Contains(detail.BookedRows, item => item.PerformanceId == 102);
    }

    [Fact]
    public void FocusedTimeline_LabelsBookingAsOpleidingAndStopsAfterNext()
    {
        var admin = AdminCase(200, 2m, Thursday);
        var day = new[]
        {
            Perf("10", 100, "07:00", "08:00", 1m, 501, Thursday, description: "vorige"),
            Perf("10", 200, "09:00", "11:00", 2m, 100, Thursday, hfdTaakId: 21, description: "toolbox"),
            Perf("10", 300, "12:00", "13:00", 1m, 501, Thursday, description: "volgende"),
            Perf("10", 400, "14:00", "15:00", 1m, 501, Thursday, description: "later"),
        };

        var focused = PayrollProject100WorkbenchBuilder.BuildDetail(admin, day, [], gps: null).FocusedContext!;
        Assert.Contains(focused.Booking, item => item.IsSelected100 && item.Badge == "OPLEIDING");
        Assert.Contains(focused.After, item => item.Start == At(Thursday, "12:00"));
        Assert.DoesNotContain(focused.After, item => item.Start == At(Thursday, "14:00"));
        Assert.True(focused.HasMoreThanFocused);
    }

    [Fact]
    public void Decisions_BookingCorrectTerminal_HoursWrongOpensActions()
    {
        var valid = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P100Valid,
            PayrollReviewCategory.Project100);
        Assert.Equal("Boeking is correct", valid.Label);
        Assert.Equal(PayrollFindingStatus.Reviewed, valid.ResultStatus);

        var hours = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P100HoursWrong,
            PayrollReviewCategory.Project100);
        Assert.Equal("Uren zijn fout", hours.Label);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, hours.ResultStatus);

        var planning = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P100PlanningMissing,
            PayrollReviewCategory.Project100);
        Assert.Equal("Planning ontbreekt", planning.Label);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, planning.ResultStatus);

        var uncertain = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P100Uncertain,
            PayrollReviewCategory.Project100);
        Assert.True(uncertain.RequiresComment);
    }

    [Fact]
    public void WorkbenchPage_WiresProject100HandlersAndDetailFacts()
    {
        var root = FindRepoRoot();
        var workbench = File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "Project100Workbench.cshtml"));
        var detail = File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "_Project100Detail.cshtml"));

        Assert.Contains("Workbench/Project100", workbench, StringComparison.Ordinal);
        Assert.Contains("P100HoursWrong", File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "Project100Workbench.cshtml.cs")), StringComparison.Ordinal);
        Assert.Contains("OPLEIDING / TOOLBOX", detail, StringComparison.Ordinal);
        Assert.Contains("Dagtotaal", detail, StringComparison.Ordinal);
        Assert.Contains("Collega's", detail, StringComparison.Ordinal);
        Assert.Contains("Volledige dag tonen", detail, StringComparison.Ordinal);
        Assert.Contains("Betaalbare overuren door opleiding", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryNonTrainingOvertime_PreservedInTrainingOvertimeFindingTarget()
    {
        var training = Perf("10", 100, "09:00", "10:00", 1m, 100, Thursday, hfdTaakId: 21);
        var findings = SpecialProjectTimeControl.Evaluate(
            [training],
            [],
            new Dictionary<string, decimal?> { ["10"] = 2.5m });

        var ot = Assert.Single(findings, item => item.FindingType == PayrollFindingType.Project100TrainingInOvertime);
        // contribution = min(1, 2.5)=1; remaining ordinary overtime target = 1.5
        Assert.Equal(1.5m, ot.SuggestedOvertimeAdjustmentHours);
        Assert.Equal(2.5m, ot.LegacyDifferenceHours);
    }

    private static PayrollAdminCase AdminCase(long perfId, decimal booked, DateOnly day)
    {
        var finding = Finding("p100", perfId, booked, day);
        return Assert.Single(PayrollAdminCaseBuilder.Build(
            PayrollReviewCaseBuilder.Build([finding], Emp("10", "Jarno"), [])));
    }

    private static PayrollAdminCase AdminCaseMulti(long[] perfIds, decimal booked, DateOnly day)
    {
        var finding = Finding("p100-multi", perfIds[0], booked, day);
        finding.RelatedPerformanceIdsJson = "[" + string.Join(',', perfIds) + "]";
        return Assert.Single(PayrollAdminCaseBuilder.Build(
            PayrollReviewCaseBuilder.Build([finding], Emp("10", "Jarno"), [])));
    }

    private static Dictionary<string, PayrollShadowEmployeeResult> Emp(string id, string name) =>
        new(StringComparer.Ordinal)
        {
            [id] = new() { ResourceId = id, DisplayNameSnapshot = name },
        };

    private static PayrollFindingRecord Finding(string key, long perfId, decimal booked, DateOnly day) =>
        new()
        {
            Id = Math.Abs(key.GetHashCode()) % 100000 + 1,
            FindingKey = key,
            ResourceId = "10",
            Date = day,
            FindingType = PayrollFindingType.Project100TrainingHours,
            Severity = PayrollFindingSeverity.Info,
            Status = PayrollFindingStatus.Open,
            Title = "Toolbox/opleiding uren",
            Description = $"Project 100 training/toolbox geboekt: {booked.ToString("0.00", Belgian)}.",
            Evidence = $"PerformanceId={perfId}; HFDTAAK=21; desc=toolbox; memo=—; planning=none",
            SuggestedAction = "Training/toolbox mag geen overuren genereren; controleer impact op verschil.",
            RelatedPerformanceIdsJson = $"[{perfId}]",
            BookedHours = booked,
            SuggestedProjectId = "100",
        };

    private static NormalizedPerformanceEntry Perf(
        string resourceId,
        long id,
        string start,
        string end,
        decimal hours,
        int projectNumber,
        DateOnly day,
        string? description = null,
        int hfdTaakId = 1)
    {
        var startTime = TimeOnly.Parse(start, CultureInfo.InvariantCulture);
        var endTime = TimeOnly.Parse(end, CultureInfo.InvariantCulture);
        return new NormalizedPerformanceEntry(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(CultureInfo.InvariantCulture),
            ResourceId: resourceId,
            Date: day,
            Start: At(day, start),
            End: At(day, end),
            AtlHoursRaw: hours,
            AtlMinutesExact: hours * 60m,
            GrossClockDuration: endTime.ToTimeSpan() - startTime.ToTimeSpan(),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: hfdTaakId,
            ProjectId: $"P{projectNumber}",
            ProjectNumber: projectNumber,
            BonNr: "B1",
            Description: description,
            Memo: null,
            Postcode: null,
            SortKey: id,
            IsAbsence: false);
    }

    private static PayrollPlanningReservation Reservation(
        int taskTypeId,
        int projectNumber,
        string from,
        string to,
        string? subject,
        DateOnly day) =>
        new(
            IdCalendar: 9000 + taskTypeId,
            ResourceId: "10",
            Date: day,
            TimeFrom: TimeOnly.Parse(from, CultureInfo.InvariantCulture),
            TimeTo: TimeOnly.Parse(to, CultureInfo.InvariantCulture),
            TaskTypeId: taskTypeId,
            TaskTypeName: $"type-{taskTypeId}",
            ProjectId: $"P{projectNumber}",
            ProjectNumber: projectNumber,
            HfdTaakId: null,
            Subject: subject,
            Classification: PayrollPlanningClassifier.Classify(taskTypeId));

    private static DateTimeOffset At(DateOnly day, string hhmm)
    {
        var time = TimeOnly.Parse(hhmm, CultureInfo.InvariantCulture);
        return new DateTimeOffset(day.ToDateTime(time), TimeSpan.Zero);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}

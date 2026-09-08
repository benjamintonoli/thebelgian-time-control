using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollProject200WorkbenchTests
{
    private static readonly DateOnly Day = new(2026, 8, 28); // vrijdag
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    [Fact]
    public void ExactMatchingPlanning_ShowsBookedPlannedDifferenceAndSuggestsAdjust()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "08:00", "10:00", 2m) };
        var planning = new[] { Reservation(26, 200, "08:00", "09:30", "Kantoor") };

        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(admin, day, planning, gps: null);
        Assert.NotNull(detail.PlanningComparison);
        var cmp = detail.PlanningComparison!;

        Assert.True(cmp.HasMatchingPlanning);
        Assert.Equal(1, cmp.MatchingCount);
        Assert.Contains("08:00–10:00", cmp.BookedSummary, StringComparison.Ordinal);
        Assert.Contains("08:00–09:30", cmp.PlannedSummary, StringComparison.Ordinal);
        Assert.Equal("+30 min", cmp.DifferenceSummary);
        Assert.Equal(30, cmp.DifferenceMinutes);
        Assert.Equal(new TimeOnly(8, 0), cmp.SuggestedAdjustStart);
        Assert.Equal(new TimeOnly(9, 30), cmp.SuggestedAdjustEnd);
        Assert.Equal(
            "Project 200 geboekt 08:00–10:00 · 2u00. Planning 08:00–09:30 · 1u30. Verschil +30 min.",
            cmp.TopSummary);
        Assert.Equal(cmp.TopSummary, detail.FocusedContext?.ContextSummary);
    }

    [Fact]
    public void NoPlanning_ShowsGeenPassendePlanning()
    {
        var admin = AdminCase(101, 0.75m);
        var day = new[] { Performance(101, "14:00", "14:45", 0.75m) };

        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        Assert.NotNull(detail.PlanningComparison);
        var cmp = detail.PlanningComparison!;

        Assert.False(cmp.HasMatchingPlanning);
        Assert.Equal("Geen passende planning gevonden", cmp.DifferenceSummary);
        Assert.Equal(
            "Project 200 geboekt 14:00–14:45 · 0u45. Geen passende planning gevonden.",
            cmp.TopSummary);
        Assert.Null(cmp.SuggestedAdjustStart);
    }

    [Fact]
    public void PlannedLongerThanBooked_ShowsNegativeDifference()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };
        var planning = new[] { Reservation(26, 200, "08:00", "10:00", "Vergadering") };

        var cmp = PayrollProject200WorkbenchBuilder.BuildDetail(admin, day, planning, gps: null).PlanningComparison!;

        Assert.Equal("-60 min", cmp.DifferenceSummary);
        Assert.Equal(-60, cmp.DifferenceMinutes);
    }

    [Fact]
    public void MultiplePlanningCandidates_KeepsReviewableWithoutWeakAutoPick()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "08:00", "10:00", 2m) };
        var planning = new[]
        {
            Reservation(26, 200, "08:00", "09:00", "Garage"),
            Reservation(1, 200, "09:00", "10:30", "Meeting"),
        };

        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(admin, day, planning, gps: null);
        var cmp = detail.PlanningComparison!;

        Assert.Equal(2, cmp.MatchingCount);
        Assert.Contains("2 passende reservaties", cmp.TopSummary, StringComparison.Ordinal);
        Assert.Null(cmp.SuggestedAdjustStart);
        Assert.Equal(2, detail.DayPlanningRows.Count(item => item.IsMatchingSupport));
    }

    [Fact]
    public void TechnicianExplanation_DedupesOmschrMemo()
    {
        var admin = AdminCase(101, 1m);
        var day = new[]
        {
            Performance(101, "08:00", "09:00", 1m, description: "HR overleg", memo: "HR overleg"),
        };

        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            bonTechnicianRemark: "HR overleg");

        Assert.NotNull(detail.TechnicianContext);
        Assert.True(detail.TechnicianContext!.HasAnyTechnicianText);
        var remark = Assert.Single(detail.TechnicianContext.PerformanceRemarks);
        Assert.Equal("HR overleg", remark.PrestOmschr);
        Assert.False(remark.ShowPrestMemo);
        Assert.Equal("HR overleg", detail.TechnicianContext.BonTechnicianRemark);
    }

    [Fact]
    public void Weekday_DutchFridayAbbreviationOnAdminCaseDate()
    {
        Assert.Equal("vr", PayrollDisplayFormatting.DutchWeekdayAbbreviation(Day));
        Assert.Equal("vr 28/08", PayrollDisplayFormatting.DateWithWeekdayShort(Day));
    }

    [Fact]
    public void FocusedContext_StopsAfterFirstRelevantNext()
    {
        var admin = AdminCase(200, 1m);
        var day = new[]
        {
            Performance(100, "07:00", "08:00", 1m, projectNumber: 501, description: "vorige"),
            Performance(200, "09:00", "10:00", 1m, description: "200"),
            Performance(300, "11:00", "12:00", 1m, projectNumber: 501, description: "volgende"),
            Performance(400, "13:00", "14:00", 1m, projectNumber: 501, description: "later"),
        };

        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        Assert.NotNull(detail.FocusedContext);
        var focused = detail.FocusedContext!;

        Assert.True(focused.HasMoreThanFocused);
        Assert.Contains(focused.Booking, item => item.IsSelected200);
        Assert.DoesNotContain(focused.After, item => item.Start == At("13:00"));
        Assert.Contains(focused.FullDay, item => item.Start == At("13:00"));
        Assert.Contains(focused.After, item => item.Start == At("11:00"));
    }

    [Fact]
    public void Decisions_MapToTerminalFollowUpAndHoursReview()
    {
        var valid = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P200Valid,
            PayrollReviewCategory.Project200);
        Assert.Equal("Werk was terecht", valid.Label);
        Assert.Equal(PayrollFindingStatus.Reviewed, valid.ResultStatus);

        var missing = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P200PlanningMissing,
            PayrollReviewCategory.Project200);
        Assert.Equal("Planning ontbreekt", missing.Label);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, missing.ResultStatus);

        var hours = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P200HoursReview,
            PayrollReviewCategory.Project200);
        Assert.Equal("Uren zijn fout", hours.Label);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, hours.ResultStatus);
        Assert.False(hours.RequiresComment);

        var uncertain = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P200Uncertain,
            PayrollReviewCategory.Project200);
        Assert.Equal("Onzeker", uncertain.Label);
        Assert.True(uncertain.RequiresComment);
    }

    [Fact]
    public void CorrectionTargets_IncludeSupportedDeleteAlways()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, hfdTaakId: 1) };
        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.Contains(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject200CorrectionCapability.SupportedDelete
                && item.PerformanceId == 101);
    }

    [Fact]
    public void Correction_SupportedVanTot_PrefillsPlanningSuggestion()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "08:00", "10:00", 2m, hfdTaakId: 23) };
        var planning = new[] { Reservation(26, 200, "08:00", "09:30", "Kantoor") };
        var activities = new Dictionary<long, PayrollProject200ResolvedActivity>
        {
            [101] = new(101, PayrollStandbyActivityTypes.WaitingTime, true, "ok", "Wachten"),
        };

        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(
            admin,
            day,
            planning,
            gps: null,
            activityByPerformanceId: activities);

        var adjust = Assert.Single(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject200CorrectionCapability.SupportedVanTot);
        Assert.Equal(new TimeOnly(8, 0), adjust.SuggestedAdjustStart);
        Assert.Equal(new TimeOnly(9, 30), adjust.SuggestedAdjustEnd);
        Assert.True(detail.HasSupportedTimeCorrection);
    }

    [Fact]
    public void FindMatchingReservations_ReusesCanonicalMatcher()
    {
        var perf = Performance(101, "08:00", "10:00", 2m);
        var dayPlanning = new[]
        {
            Reservation(26, 200, "08:00", "09:30", "match"),
            Reservation(26, 501, "08:00", "09:30", "other project"),
            Reservation(3, 200, "08:00", "09:30", "absence"),
        };

        var matching = SpecialProjectTimeControl.FindMatchingReservations(perf, dayPlanning);
        Assert.Equal(2, matching.Count);
        Assert.Contains(matching, item => item.Subject == "match");
        Assert.Contains(matching, item => item.Subject == "absence");
        Assert.DoesNotContain(matching, item => item.Subject == "other project");
    }

    [Fact]
    public void GpsNeverAutoValidates()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };
        var detail = PayrollProject200WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            includeTechnicalDiagnostics: true);

        Assert.Contains(PayrollProject200CaseDetail.GpsNeverValidatesNote, detail.TechnicalCollapsedNotes);
    }

    [Fact]
    public void Project200WorkbenchMarkup_HasHoursInlineAndPlanningBlocks()
    {
        var root = FindRepoRoot();
        var workbench = File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "Project200Workbench.cshtml"));
        var detail = File.ReadAllText(Path.Combine(root, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "_Project200Detail.cshtml"));

        Assert.Contains("P200HoursReview", workbench, StringComparison.Ordinal);
        Assert.Contains("Project200Workbench", workbench, StringComparison.Ordinal);
        Assert.Contains("Geboekt", detail, StringComparison.Ordinal);
        Assert.Contains("Geen passende planning gevonden", detail, StringComparison.Ordinal);
        Assert.Contains("Uitleg technieker", detail, StringComparison.Ordinal);
        Assert.Contains("Volledige dag tonen", detail, StringComparison.Ordinal);
        Assert.Contains("Voorstel:", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("P300HoursWrong", workbench, StringComparison.Ordinal);
    }

    private static PayrollAdminCase AdminCase(long perfId, decimal booked)
    {
        var finding = Finding("p200", perfId, booked);
        return Assert.Single(PayrollAdminCaseBuilder.Build(
            PayrollReviewCaseBuilder.Build([finding], Emp("10", "Jarno"), [])));
    }

    private static Dictionary<string, PayrollShadowEmployeeResult> Emp(string id, string name) =>
        new(StringComparer.Ordinal)
        {
            [id] = new() { ResourceId = id, DisplayNameSnapshot = name },
        };

    private static PayrollFindingRecord Finding(string key, long perfId, decimal booked) =>
        new()
        {
            Id = Math.Abs(key.GetHashCode()) % 100000 + 1,
            FindingKey = key,
            ResourceId = "10",
            Date = Day,
            FindingType = PayrollFindingType.Project200WithoutPlanning,
            Severity = PayrollFindingSeverity.Review,
            Status = PayrollFindingStatus.Open,
            Title = "Project 200 zonder planning",
            Description = $"Geboekt 08:00-09:00 ({booked.ToString("0.00", Belgian)} u) zonder ondersteunende planning.",
            Evidence = $"PerformanceId={perfId}; PROJNR=200; desc=werk; memo=—; planning evidence = none.",
            SuggestedAction = "Controleer",
            RelatedPerformanceIdsJson = $"[{perfId}]",
            BookedHours = booked,
            SuggestedProjectId = "200",
        };

    private static NormalizedPerformanceEntry Performance(
        long id,
        string start,
        string end,
        decimal hours,
        int projectNumber = 200,
        string? description = null,
        string? memo = null,
        int hfdTaakId = 1,
        string? bonNr = "B1")
    {
        var startTime = TimeOnly.Parse(start, CultureInfo.InvariantCulture);
        var endTime = TimeOnly.Parse(end, CultureInfo.InvariantCulture);
        return new NormalizedPerformanceEntry(
            SourceEntryId: id,
            SourceEntryKey: id.ToString(CultureInfo.InvariantCulture),
            ResourceId: "10",
            Date: Day,
            Start: At(start),
            End: At(end),
            AtlHoursRaw: hours,
            AtlMinutesExact: hours * 60m,
            GrossClockDuration: endTime.ToTimeSpan() - startTime.ToTimeSpan(),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: hfdTaakId,
            ProjectId: $"P{projectNumber}",
            ProjectNumber: projectNumber,
            BonNr: bonNr,
            Description: description,
            Memo: memo,
            Postcode: null,
            SortKey: id,
            IsAbsence: false);
    }

    private static PayrollPlanningReservation Reservation(
        int taskTypeId,
        int projectNumber,
        string from,
        string to,
        string? subject) =>
        new(
            IdCalendar: projectNumber * 1000L + taskTypeId + subject!.GetHashCode(StringComparison.Ordinal) % 100,
            ResourceId: "10",
            Date: Day,
            TimeFrom: TimeOnly.Parse(from, CultureInfo.InvariantCulture),
            TimeTo: TimeOnly.Parse(to, CultureInfo.InvariantCulture),
            TaskTypeId: taskTypeId,
            TaskTypeName: $"type-{taskTypeId}",
            ProjectId: $"P{projectNumber}",
            ProjectNumber: projectNumber,
            HfdTaakId: null,
            Subject: subject,
            Classification: PayrollPlanningClassifier.Classify(taskTypeId));

    private static DateTimeOffset At(string hhmm)
    {
        var time = TimeOnly.Parse(hhmm, CultureInfo.InvariantCulture);
        return new DateTimeOffset(Day.ToDateTime(time), TimeSpan.Zero);
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

    [Fact]
    public void Project200_UrenFout_OpensInlineWithoutRequiredComment()
    {
        var hours = Assert.Single(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project200)
                .Where(item => item.DecisionCode == PayrollGuidedDecisionCodes.P200HoursReview));
        Assert.Equal("Uren zijn fout", hours.Label);
        Assert.False(hours.RequiresComment);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, hours.ResultStatus);
    }
}

using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollProject300WorkbenchTests
{
    private static readonly DateOnly Day = new(2026, 8, 28);
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    [Fact]
    public void AuthoritativeVanTot_ComesFromNormalizedPerformanceEntry()
    {
        var admin = AdminCase(perfId: 101, booked: 1.5m);
        var day = new[]
        {
            Performance(101, "08:00", "09:30", 1.5m, description: "klaarzet", memo: "magazijn"),
            Performance(999, "10:00", "11:00", 9m, projectNumber: 200),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        var row = Assert.Single(detail.BookedRows);

        Assert.Equal(101, row.PerformanceId);
        Assert.Equal(At("08:00"), row.Start);
        Assert.Equal(At("09:30"), row.End);
        Assert.Equal(1.5m, row.AtlHours);
        Assert.True(row.IsSelected);
    }

    [Fact]
    public void MultiIntervals_ListsAllSelectedAndTotalsHours()
    {
        var findings = new[]
        {
            Finding("a", 1, 1m),
            Finding("b", 2, 0.66m),
            Finding("c", 3, 1m),
        };
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build(
            PayrollReviewCaseBuilder.Build(findings, Emp("10", "Jarno"), [])));
        var day = new[]
        {
            Performance(1, "08:00", "09:00", 1m),
            Performance(2, "10:00", "10:40", 0.66m),
            Performance(3, "13:00", "14:00", 1m),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.Equal(3, detail.BookedRows.Count);
        Assert.Equal(2.66m, detail.TotalAtlHours);
        Assert.Equal([1L, 2L, 3L], detail.BookedRows.Select(item => item.PerformanceId).ToArray());
    }

    [Fact]
    public void OmshrMemo_SurfacedOnBookedRows()
    {
        var admin = AdminCase(101, 1m);
        var day = new[]
        {
            Performance(101, "08:00", "09:00", 1m, description: "Interne klaarzet", memo: "magazijn note"),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        var row = Assert.Single(detail.BookedRows);

        Assert.Equal("Interne klaarzet", row.Description);
        Assert.Equal("magazijn note", row.Memo);
    }

    [Fact]
    public void UnrelatedPlanning_DoesNotValidate_MatchingRemainsGeen()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "08:00", "10:00", 2m) };
        var planning = new[]
        {
            Reservation(taskTypeId: 26, projectNumber: 501, from: "08:00", to: "12:00", subject: "Andere job"),
            Reservation(taskTypeId: 3, projectNumber: 300, from: "13:00", to: "14:00", subject: "Verlof"),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, planning, gps: null);

        Assert.Equal("Geen", detail.MatchingReservationLabel);
        Assert.All(detail.DayPlanningRows, row => Assert.False(row.IsMatchingSupport));
        Assert.DoesNotContain(detail.DayPlanningRows, row => row.Classification == PayrollPlanningClassification.Absence);
        Assert.Contains(detail.DayPlanningRows, row => row.Label.Contains("Andere job", StringComparison.Ordinal));
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.TechnicalCollapsedNotes);
    }

    [Fact]
    public void PreviousAndNextNeighbors_AroundSelectedWindow()
    {
        var admin = AdminCase(200, 1m);
        var day = new[]
        {
            Performance(100, "07:00", "08:00", 1m, projectNumber: 501, description: "vorige"),
            Performance(200, "09:00", "10:00", 1m, description: "300"),
            Performance(300, "11:00", "12:00", 1m, projectNumber: 200, description: "volgende"),
            Performance(400, "13:00", "14:00", 1m, isAbsence: true, description: "afwezig"),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.Equal(
            [
                PayrollProject300TimelineKind.Previous,
                PayrollProject300TimelineKind.Selected,
                PayrollProject300TimelineKind.Next,
            ],
            detail.NeighborTimelineRows.Select(item => item.Kind).ToArray());
        Assert.Equal(100, detail.NeighborTimelineRows[0].PerformanceId);
        Assert.Equal(200, detail.NeighborTimelineRows[1].PerformanceId);
        Assert.True(detail.NeighborTimelineRows[1].IsSelected300);
        Assert.Equal(300, detail.NeighborTimelineRows[2].PerformanceId);
        Assert.DoesNotContain(detail.NeighborTimelineRows, item => item.PerformanceId == 400);
    }

    [Fact]
    public void MissingGps_IsExplicitUnavailable()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.False(detail.GpsContext.Available);
        Assert.Equal(PayrollProject300WorkbenchBuilder.MissingGpsSummary, detail.GpsContext.Summary);
        Assert.Empty(detail.GpsContext.Events);
    }

    [Fact]
    public void GpsNeverAutoValidates_EvenWhenTripsPresent()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "10:00", "12:00", 2m) };
        var gps = new StandbyGpsDayEvidence(
            "10",
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ-9",
            RegistrationPlate: "1-XYZ-999",
            MappingReason: "Resolved",
            Trips:
            [
                new StandbyGpsTripEvidence(
                    "t1",
                    At("09:30"),
                    At("10:30"),
                    DistanceKilometres: 4.2m,
                    DrivingMinutes: 20,
                    StartAddress: "Depot A",
                    EndAddress: "Site B",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
            ],
            MappingKind: "Plate");

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps);

        Assert.True(detail.GpsContext.Available);
        Assert.Equal("Geen", detail.MatchingReservationLabel);
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.GpsContext.Summary);
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.TechnicalCollapsedNotes);
        Assert.Contains(detail.GpsContext.Events, item =>
            item.Label == "Tijdens geboekte 300-tijd" && item.Phase == "During");
        Assert.Contains(detail.GpsContext.Events, item =>
            (item.Detail ?? string.Empty).Contains("Depot A", StringComparison.Ordinal)
            || item.Label.Contains("Depot A", StringComparison.Ordinal));
        Assert.DoesNotContain(detail.GpsContext.Events, item =>
            ((item.Detail ?? string.Empty) + item.Label).Contains("thuis", StringComparison.OrdinalIgnoreCase)
            && !((item.Detail ?? string.Empty) + item.Label).Contains("Depot", StringComparison.Ordinal));
    }

    [Fact]
    public void GpsPending_ShowsLoadingState_WithoutBlockingDetail()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            gpsPending: true);

        Assert.Single(detail.BookedRows);
        Assert.True(detail.GpsContext.IsLoading);
        Assert.False(detail.GpsContext.Available);
        Assert.Equal(PayrollProject300WorkbenchBuilder.GpsLoadingSummary, detail.GpsContext.Summary);
        Assert.Equal("Pending", detail.GpsContext.MappingKind);
        Assert.Empty(detail.GpsContext.Events);
    }

    [Fact]
    public void GpsContext_HumanBeforeDuringAfter_Labels()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "10:00", "12:00", 2m) };
        var gps = new StandbyGpsDayEvidence(
            "10",
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ-9",
            RegistrationPlate: "1-XYZ-999",
            MappingReason: "Resolved",
            Trips:
            [
                new StandbyGpsTripEvidence(
                    "before",
                    At("09:00"),
                    At("09:30"),
                    DistanceKilometres: 0.2m,
                    DrivingMinutes: 5,
                    StartAddress: "Depot A",
                    EndAddress: "Meise stilstand",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
                new StandbyGpsTripEvidence(
                    "during",
                    At("10:15"),
                    At("11:00"),
                    DistanceKilometres: 3.5m,
                    DrivingMinutes: 20,
                    StartAddress: "Werf Noord",
                    EndAddress: "Werf Noord",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
                new StandbyGpsTripEvidence(
                    "after",
                    At("12:10"),
                    At("12:40"),
                    DistanceKilometres: 8.0m,
                    DrivingMinutes: 25,
                    StartAddress: "Werf Noord",
                    EndAddress: "Magazijn Zuid",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
            ],
            MappingKind: "Plate");

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps);

        Assert.True(detail.GpsContext.Available);
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.GpsContext.Summary);

        var before = Assert.Single(detail.GpsContext.Events, item => item.Phase == "Before");
        Assert.Contains("Voor: stilstand", before.Label, StringComparison.Ordinal);
        Assert.Contains("Meise stilstand", before.Label, StringComparison.Ordinal);

        var during = Assert.Single(detail.GpsContext.Events, item => item.Phase == "During");
        Assert.Equal("Tijdens geboekte 300-tijd", during.Label);
        Assert.Contains("Werf Noord", during.Detail!, StringComparison.Ordinal);

        Assert.Contains(
            detail.GpsContext.Events,
            item => item.Phase == "After" && item.Label == "Na: vertrek");
        Assert.Contains(
            detail.GpsContext.Events,
            item => item.Phase == "After" && item.Label.StartsWith("Na: aankomst:", StringComparison.Ordinal)
                && item.Label.Contains("Magazijn Zuid", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityDictionary_EnablesCustomerWorkSupport()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, hfdTaakId: 7) };
        var activities = new Dictionary<long, PayrollProject300ResolvedActivity>
        {
            [101] = new(
                PerformanceId: 101,
                ActivityType: "CustomerWork",
                Supported: true,
                Message: "VAN/TOT-correctie beschikbaar (CustomerWork via HFDTAAK 7).",
                FriendlyTaskName: "HFDTAAK 7 (CW: Klantwerk)"),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            activityByPerformanceId: activities);

        var supported = Assert.Single(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.SupportedVanTot);
        Assert.Equal("CustomerWork", supported.ActivityType);
        Assert.Equal("HFDTAAK 7 (CW: Klantwerk)", supported.FriendlyTaskName);
        Assert.Contains(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.ZeroDeleteUnavailable);
    }

    [Fact]
    public void BuildProjectDisplayLabel_Project300()
    {
        Assert.Equal(
            "Project 300",
            PayrollProject300WorkbenchBuilder.BuildProjectDisplayLabel(300, projectId: "49432"));
        Assert.Equal(
            "BON 7788",
            PayrollProject300WorkbenchBuilder.BuildProjectDisplayLabel(300, projectId: "49432", bonNr: "7788"));
        Assert.Equal("—", PayrollProject300WorkbenchBuilder.BuildProjectDisplayLabel(null, projectId: "49432"));
    }

    [Fact]
    public void BookedRows_SetProjectDisplayLabelAndProjectNumber()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        var row = Assert.Single(detail.BookedRows);

        Assert.Equal(300, row.ProjectNumber);
        Assert.Equal("Project 300", row.ProjectDisplayLabel);
    }

    [Fact]
    public void WorkWasTerecht_StillReviewed()
    {
        var choice = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P300WorkValid,
            PayrollReviewCategory.Project300);
        Assert.Equal("Werk was terecht", choice.Label);
        Assert.Equal(PayrollFindingStatus.Reviewed, choice.ResultStatus);
        Assert.False(choice.RequiresComment);
    }

    [Fact]
    public void UrenFout_RequiresComment()
    {
        var choice = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P300HoursWrong,
            PayrollReviewCategory.Project300);
        Assert.Equal("Uren zijn fout", choice.Label);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, choice.ResultStatus);
        Assert.True(choice.RequiresComment);
    }

    [Fact]
    public void Correction_Unsupported_ForNon23()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, hfdTaakId: 1) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.Contains(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.UnsupportedActivity
                && item.CapabilityMessage == PayrollProject300WorkbenchBuilder.UnsupportedActivityMessage
                && item.ActivityType is null);
    }

    [Fact]
    public void Correction_Supported_ForHfdTaakId23()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, hfdTaakId: 23) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        var supported = Assert.Single(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.SupportedVanTot);
        Assert.Equal(PayrollStandbyActivityTypes.WaitingTime, supported.ActivityType);
        Assert.Equal(23, supported.HfdTaakId);
    }

    [Fact]
    public void ZeroDelete_Unavailable_AlwaysPresent()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, hfdTaakId: 23) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.Contains(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.ZeroDeleteUnavailable
                && item.CapabilityMessage == PayrollProject300WorkbenchBuilder.ZeroDeleteUnavailableMessage
                && item.PerformanceId == 101);
    }

    [Fact]
    public void Project300_ChoiceLabels_MatchWorkbenchV3()
    {
        var labels = PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project300)
            .Select(item => item.Label)
            .ToArray();
        Assert.Equal(["Werk was terecht", "Planning ontbreekt", "Uren zijn fout", "Onzeker"], labels);
    }

    [Fact]
    public void DaySourceCache_ReusesResourceIdAndDate()
    {
        var cache = new PayrollProject300DaySourceCache();
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };
        cache.Set("10", Day, day, []);

        Assert.True(cache.TryGet("10", Day, out var performances, out var planning));
        Assert.Same(day, performances);
        Assert.Empty(planning);
        Assert.False(cache.TryGet("10", Day.AddDays(1), out _, out _));
    }

    [Fact]
    public void GpsContextCache_ReusesAdminCaseKey()
    {
        var cache = new PayrollProject300GpsContextCache();
        var context = new PayrollProject300GpsContext(
            Available: true,
            Summary: "cached",
            Events: [],
            MappingKind: "Plate",
            ObjectIdCollapsed: "OBJ",
            TripsCollapsed: []);
        cache.Set("case-a", context);

        Assert.True(cache.TryGet("case-a", out var hit));
        Assert.Same(context, hit);
        Assert.False(cache.TryGet("case-b", out _));
    }

    [Fact]
    public void GpsEvidenceCache_ReusesResourceIdAndDate()
    {
        var cache = new PayrollProject300GpsCache();
        var evidence = new StandbyGpsDayEvidence(
            "10",
            Day,
            HasVehicleMapping: true,
            MappingAmbiguous: false,
            ObjectId: "OBJ",
            RegistrationPlate: "1-AAA-111",
            MappingReason: "Resolved",
            Trips: [],
            MappingKind: "Plate");
        cache.Set("10", Day, evidence);

        Assert.True(cache.TryGet("10", Day, out var hit, out var wasHit));
        Assert.True(wasHit);
        Assert.Same(evidence, hit);
        Assert.True(cache.Has("10", Day));
        Assert.False(cache.Has("11", Day));
    }

    private static PayrollAdminCase AdminCase(long perfId, decimal booked)
    {
        var finding = Finding("p300", perfId, booked);
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
            FindingType = PayrollFindingType.Project300WithoutPlanning,
            Severity = PayrollFindingSeverity.Review,
            Status = PayrollFindingStatus.Open,
            Title = "Project 300 zonder planning",
            Description = $"Geboekt 08:00-09:00 ({booked.ToString("0.00", Belgian)} u) zonder ondersteunende planning.",
            Evidence = $"PerformanceId={perfId}; PROJNR=300; desc=werk; memo=—; planning evidence = none.",
            SuggestedAction = "Controleer",
            RelatedPerformanceIdsJson = $"[{perfId}]",
            BookedHours = booked,
            SuggestedProjectId = "300",
        };

    private static NormalizedPerformanceEntry Performance(
        long id,
        string start,
        string end,
        decimal hours,
        int projectNumber = 300,
        string? description = null,
        string? memo = null,
        int hfdTaakId = 1,
        bool isAbsence = false)
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
            BonNr: null,
            Description: description,
            Memo: memo,
            Postcode: null,
            SortKey: id,
            IsAbsence: isAbsence);
    }

    private static PayrollPlanningReservation Reservation(
        int taskTypeId,
        int projectNumber,
        string from,
        string to,
        string? subject) =>
        new(
            IdCalendar: projectNumber * 1000L + taskTypeId,
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
}

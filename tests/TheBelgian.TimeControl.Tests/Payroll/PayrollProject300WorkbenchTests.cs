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

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            admin,
            day,
            planning,
            gps: null,
            includeTechnicalDiagnostics: true);

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

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps,
            includeTechnicalDiagnostics: true);

        Assert.True(detail.GpsContext.Available);
        Assert.Equal("Geen", detail.MatchingReservationLabel);
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.GpsContext.Summary);
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.TechnicalCollapsedNotes);
        Assert.Contains(detail.GpsContext.Events, item =>
            item.Label.Contains("Vertrek", StringComparison.Ordinal)
            || item.Label.Contains("Aankomst", StringComparison.Ordinal)
            || item.Label.Contains("stilstand", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detail.GpsContext.Events, item =>
            ((item.Detail ?? string.Empty) + item.Label).Contains("Depot A", StringComparison.Ordinal)
            || item.Label.Contains("Depot", StringComparison.Ordinal));
        Assert.DoesNotContain(detail.GpsContext.Events, item =>
            ((item.Detail ?? string.Empty) + item.Label).Contains("thuis", StringComparison.OrdinalIgnoreCase)
            && !((item.Detail ?? string.Empty) + item.Label).Contains("Depot", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionDetail_OmitsTechnicalDiagnosticsByDefault()
    {
        var admin = AdminCase(101, 2m);
        var day = new[] { Performance(101, "10:00", "12:00", 2m) };
        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);
        Assert.Empty(detail.TechnicalCollapsedNotes);
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
                    DistanceKilometres: 4.2m,
                    DrivingMinutes: 20,
                    StartAddress: "1785 Merchtem",
                    EndAddress: "1861 Meise",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
                new StandbyGpsTripEvidence(
                    "during",
                    At("10:15"),
                    At("11:00"),
                    DistanceKilometres: 3.5m,
                    DrivingMinutes: 20,
                    StartAddress: "1861 Meise",
                    EndAddress: "1000 Brussel",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
                new StandbyGpsTripEvidence(
                    "after",
                    At("12:10"),
                    At("12:40"),
                    DistanceKilometres: 8.0m,
                    DrivingMinutes: 25,
                    StartAddress: "1000 Brussel",
                    EndAddress: "2800 Mechelen",
                    ObjectId: "OBJ-9",
                    VehiclePlate: "1-XYZ-999"),
            ],
            MappingKind: "Plate");

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps);

        Assert.True(detail.GpsContext.Available);
        Assert.Contains(PayrollProject300CaseDetail.GpsNeverValidatesNote, detail.GpsContext.Summary);

        Assert.Contains(detail.GpsContext.Events, item =>
            item.Phase == "Before" && item.Label.Contains("Vertrek", StringComparison.Ordinal));
        Assert.Contains(detail.GpsContext.Events, item =>
            item.Phase == "Before" && item.Label.Contains("Aankomst", StringComparison.Ordinal)
            && item.Label.Contains("Meise", StringComparison.Ordinal));

        Assert.Contains(detail.DayTimeline!, item =>
            item.Badge == "GPS" && item.Title.Contains("Vertrek", StringComparison.Ordinal)
            && item.Title.Contains("Meise", StringComparison.Ordinal));
        Assert.Contains(detail.DayTimeline!, item =>
            item.Badge == "GPS" && item.Title.Contains("Aankomst", StringComparison.Ordinal)
            && item.Title.Contains("Brussel", StringComparison.Ordinal));
        Assert.Contains(detail.DayTimeline!, item =>
            item.Badge == "GPS" && item.Title.Contains("Vertrek", StringComparison.Ordinal)
            && item.Title.Contains("Brussel", StringComparison.Ordinal));
        Assert.Contains(detail.DayTimeline!, item =>
            item.Badge == "GPS" && item.Title.Contains("Aankomst", StringComparison.Ordinal)
            && item.Title.Contains("Mechelen", StringComparison.Ordinal));
    }

    [Fact]
    public void TripChain_IncompleteDestination_DoesNotInventArrival()
    {
        var trips = new[]
        {
            new StandbyGpsTripEvidence(
                "open",
                At("09:00"),
                At("09:30"),
                DistanceKilometres: 5m,
                DrivingMinutes: 20,
                StartAddress: "1785 Merchtem",
                EndAddress: null,
                ObjectId: "OBJ",
                VehiclePlate: "1-AAA"),
        };

        var chain = PayrollProject300WorkbenchBuilder.BuildGpsTripChainEntries(
            trips,
            At("10:00"),
            At("11:00"));

        Assert.Contains(chain, item => item.Title.Contains("Vertrek", StringComparison.Ordinal));
        Assert.Contains(chain, item =>
            item.Subtitle != null
            && item.Subtitle.Contains("bestemming niet betrouwbaar", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(chain, item =>
            item.Title.StartsWith("Aankomst", StringComparison.Ordinal)
            && !item.Title.Contains("onvolledig", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExactOverlap_Wording_IncludesMinutes()
    {
        var wording = PayrollProject300WorkbenchBuilder.FormatExactOverlapWording(
            At("10:50"),
            At("11:05"),
            At("10:00"),
            At("11:00"));
        Assert.Equal("Vertrek tijdens 300-boeking", wording);

        var partial = PayrollProject300WorkbenchBuilder.FormatExactOverlapWording(
            At("09:55"),
            At("10:05"),
            At("10:00"),
            At("12:00"));
        Assert.Contains("Aankomst", partial!, StringComparison.Ordinal);
        Assert.Contains("vóór einde 300-boeking", partial!, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownLocation_InsideRadius_AliasesTheBelgian()
    {
        var catalog = new KnownLocationCatalog(
        [
            new KnownLocationDefinition(
                "The Belgian",
                "Meise",
                50.984487,
                4.300723,
                120,
                "Slozenstraat 86, 1861 Meise"),
        ]);

        var inside = catalog.Resolve(50.98450m, 4.30080m, "Slozenstraat 84, 1861 Meise");
        Assert.True(inside.MatchedByGeofence);
        Assert.Equal("The Belgian — Meise", inside.Primary);

        var outside = catalog.Resolve(50.99000m, 4.31000m, "1785 Merchtem");
        Assert.False(outside.MatchedByGeofence);
        Assert.Equal("Merchtem", outside.Primary);

        var absent = KnownLocationCatalog.Empty.Resolve(50.98450m, 4.30080m, "Slozenstraat 86, 1861 Meise");
        Assert.False(absent.MatchedByGeofence);
        Assert.Equal("Meise", absent.Primary);
    }

    [Fact]
    public void TripChain_KnownLocation_OnArrivalDeparture()
    {
        var catalog = new KnownLocationCatalog(
        [
            new KnownLocationDefinition("The Belgian", "Meise", 50.984487, 4.300723, 120, "Slozenstraat 86"),
        ]);
        var trips = new[]
        {
            new StandbyGpsTripEvidence(
                "to-hq",
                At("06:37"),
                At("06:52"),
                DistanceKilometres: 8m,
                DrivingMinutes: 15,
                StartAddress: "1785 Merchtem",
                EndAddress: "Slozenstraat 84, 1861 Meise",
                ObjectId: "OBJ",
                VehiclePlate: "1-AAA",
                StartLatitude: 50.97000m,
                StartLongitude: 4.28000m,
                EndLatitude: 50.98450m,
                EndLongitude: 4.30080m),
            new StandbyGpsTripEvidence(
                "from-hq",
                At("06:55"),
                At("07:31"),
                DistanceKilometres: 12m,
                DrivingMinutes: 36,
                StartAddress: "Slozenstraat 86, 1861 Meise",
                EndAddress: "1000 Brussel",
                ObjectId: "OBJ",
                VehiclePlate: "1-AAA",
                StartLatitude: 50.98448m,
                StartLongitude: 4.30072m,
                EndLatitude: 50.85000m,
                EndLongitude: 4.35000m),
        };

        var chain = PayrollProject300WorkbenchBuilder.BuildGpsTripChainEntries(
            trips,
            At("06:35"),
            At("06:55"),
            catalog);

        Assert.Contains(chain, item => item.Title == "Aankomst The Belgian — Meise");
        Assert.Contains(chain, item => item.Title == "Vertrek The Belgian — Meise");
        Assert.Contains(chain, item => item.Title.Contains("Aankomst", StringComparison.Ordinal)
            && item.Title.Contains("Brussel", StringComparison.Ordinal));
        Assert.Contains(chain, item =>
            item.Title == "Vertrek The Belgian — Meise"
            && item.Subtitle != null
            && (item.Subtitle.Contains("Vertrek", StringComparison.Ordinal)
                && item.Subtitle.Contains("vóór einde", StringComparison.Ordinal)
                || item.Subtitle.Contains("Vertrek tijdens", StringComparison.Ordinal)));
        Assert.Contains(chain, item =>
            item.Subtitle != null && (
                item.Subtitle.Contains("Overlapt", StringComparison.Ordinal)
                || item.Subtitle.Contains("Vertrek", StringComparison.Ordinal)
                || item.Subtitle.Contains("Aankomst", StringComparison.Ordinal)));
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
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.SupportedDelete);
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
    public void UrenFout_OpensActionPanelWithoutRequiredComment()
    {
        var choice = PayrollGuidedDecisions.Resolve(
            PayrollGuidedDecisionCodes.P300HoursWrong,
            PayrollReviewCategory.Project300);
        Assert.Equal("Uren zijn fout", choice.Label);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, choice.ResultStatus);
        Assert.False(choice.RequiresComment);
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
    public void SupportedDelete_AlwaysPresent()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, hfdTaakId: 23) };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null);

        Assert.Contains(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.SupportedDelete
                && item.CapabilityMessage == PayrollProject300WorkbenchBuilder.SupportedDeleteMessage
                && item.PerformanceId == 101);
        Assert.DoesNotContain(
            detail.CorrectionTargets,
            item => item.CorrectionCapability == PayrollProject300CorrectionCapability.ZeroDeleteUnavailable);
    }

    [Fact]
    public void TechnicianContext_SurfacesDistinctBonAndPrestSources()
    {
        var admin = AdminCase(101, 1m);
        var day = new[]
        {
            Performance(101, "08:00", "09:00", 1m, description: "prest omschr", memo: "prest memo"),
        };

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            bonTechnicianRemark: "BON technieker memo",
            includeTechnicalDiagnostics: true);

        var tech = detail.TechnicianContext;
        Assert.NotNull(tech);
        Assert.True(tech!.HasAnyTechnicianText);
        Assert.Equal("BON technieker memo", tech.BonTechnicianRemark);
        Assert.Equal(PayrollProject300TechnicianContext.BonMemoSourceField, tech.BonRemarkSourceField);
        var row = Assert.Single(tech.PerformanceRemarks);
        Assert.Equal("prest omschr", row.PrestOmschr);
        Assert.Equal("prest memo", row.PrestMemo);
        Assert.True(row.ShowPrestMemo);
        Assert.Contains(detail.TechnicalCollapsedNotes, note => note.Contains("BON.MEMO", StringComparison.Ordinal));
        Assert.Contains(detail.TechnicalCollapsedNotes, note => note.Contains("PROJ_Prest.OMSCHR", StringComparison.Ordinal));
    }

    [Fact]
    public void TechnicianContext_DedupesIdenticalPrestOmschrAndMemo()
    {
        var admin = AdminCase(101, 1m);
        var day = new[]
        {
            Performance(101, "08:00", "09:00", 1m, description: "zelfde tekst", memo: "zelfde tekst"),
        };

        var tech = PayrollProject300WorkbenchBuilder.BuildTechnicianContext(day, bonTechnicianRemark: null);
        var row = Assert.Single(tech.PerformanceRemarks);
        Assert.Equal("zelfde tekst", row.PrestOmschr);
        Assert.False(row.ShowPrestMemo);
    }

    [Fact]
    public void TechnicianContext_MultiPerformance_KeepsRemarksPerPerformanceId()
    {
        var findings = new[]
        {
            Finding("a", 1, 0.33m),
            Finding("b", 2, 1.5m),
        };
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build(
            PayrollReviewCaseBuilder.Build(findings, Emp("10", "Dimitri"), [])));
        var day = new[]
        {
            Performance(1, "14:00", "14:20", 0.33m, description: "eerste", memo: null),
            Performance(2, "14:20", "15:50", 1.5m, description: "tweede", memo: "memo-2"),
        };

        var tech = PayrollProject300WorkbenchBuilder.BuildTechnicianContext(
            day,
            bonTechnicianRemark: "Ophalen materiaal",
            fallbackBonNr: "26601932");

        Assert.Equal("Ophalen materiaal", tech.BonTechnicianRemark);
        Assert.Equal(2, tech.PerformanceRemarks.Count);
        Assert.Equal(1, tech.PerformanceRemarks[0].PerformanceId);
        Assert.Equal("eerste", tech.PerformanceRemarks[0].PrestOmschr);
        Assert.Equal(2, tech.PerformanceRemarks[1].PerformanceId);
        Assert.Equal("tweede", tech.PerformanceRemarks[1].PrestOmschr);
        Assert.Equal("memo-2", tech.PerformanceRemarks[1].PrestMemo);
    }

    [Fact]
    public void TechnicianContext_Empty_HasCleanEmptyState()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m) };
        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(admin, day, [], gps: null, bonTechnicianRemark: null);
        Assert.False(detail.TechnicianContext!.HasAnyTechnicianText);
        Assert.Equal(PayrollProject300CaseDetail.NoTechnicianRemarkMessage, PayrollProject300CaseDetail.NoTechnicianRemarkMessage);
    }

    [Fact]
    public void QueuePreview_PrefersPrestThenBon_AndTruncates()
    {
        Assert.Equal(
            "kort",
            PayrollProject300WorkbenchBuilder.BuildQueueTechnicianPreview("kort", null, "bon"));
        Assert.Equal(
            "bon tekst",
            PayrollProject300WorkbenchBuilder.BuildQueueTechnicianPreview(null, null, "bon tekst"));
        var longText = new string('x', 100);
        var preview = PayrollProject300WorkbenchBuilder.BuildQueueTechnicianPreview(longText, null, null, maxLength: 20);
        Assert.NotNull(preview);
        Assert.True(preview!.Length <= 20);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void GpsPending_StillIndependentOfTechnicianContext()
    {
        var admin = AdminCase(101, 1m);
        var day = new[] { Performance(101, "08:00", "09:00", 1m, description: "werk") };
        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            admin,
            day,
            [],
            gps: null,
            gpsPending: true,
            bonTechnicianRemark: "bon");

        Assert.True(detail.GpsContext.IsLoading);
        Assert.True(detail.TechnicianContext!.HasAnyTechnicianText);
        Assert.Single(detail.BookedRows);
    }

    [Fact]
    public void ClassifyGpsOverlap_PartialOverlap_IsNotDuring()
    {
        var selectedStart = At("10:00");
        var selectedEnd = At("10:20");
        // trip mostly outside, only 2 minutes overlap
        Assert.Equal(
            "Overlap",
            PayrollProject300WorkbenchBuilder.ClassifyGpsOverlap(
                At("09:50"),
                At("10:02"),
                selectedStart,
                selectedEnd));
        Assert.Equal(
            "Departure",
            PayrollProject300WorkbenchBuilder.ClassifyGpsOverlap(
                At("10:15"),
                At("10:40"),
                selectedStart,
                selectedEnd));
        Assert.Equal(
            "During",
            PayrollProject300WorkbenchBuilder.ClassifyGpsOverlap(
                At("10:00"),
                At("10:20"),
                selectedStart,
                selectedEnd));
    }

    [Fact]
    public void UnifiedDayTimeline_OrdersPlanningPerformanceAndHighlights300()
    {
        var admin = AdminCase(200, 1m);
        var day = new[]
        {
            Performance(100, "07:00", "08:00", 1m, projectNumber: 501, description: "vorige"),
            Performance(200, "09:00", "10:00", 1m, description: "300 note"),
            Performance(300, "11:00", "12:00", 1m, projectNumber: 200, description: "volgende"),
        };
        var planning = new[]
        {
            Reservation(taskTypeId: 26, projectNumber: 501, from: "08:00", to: "12:00", subject: "Andere job"),
        };

        var timeline = PayrollProject300WorkbenchBuilder.BuildUnifiedDayTimeline(
            selectedPerformances: [day[1]],
            dayPerformances: day,
            dayPlanning: planning,
            gps: null);

        Assert.Contains(timeline, item => item.Kind == PayrollProject300DayTimelineKind.Planning);
        Assert.Contains(timeline, item => item.IsSelected300 && item.Badge == "300");
        Assert.Contains(timeline, item => item.Kind == PayrollProject300DayTimelineKind.Performance && item.Badge == "PRESTATIE");
        Assert.True(timeline.Zip(timeline.Skip(1), (a, b) => a.SortAt <= b.SortAt).All(x => x));
    }

    [Fact]
    public void ExtractLocality_FromBelgianAddress()
    {
        Assert.Equal(
            "Merchtem",
            PayrollProject300WorkbenchBuilder.ExtractLocality("Oudstrijdersstraat 13, 1785 Merchtem, België"));
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

    [Fact]
    public void FocusedContext_HidesLaterUnrelatedDayEvents()
    {
        var selected = new[] { Performance(200, "08:40", "08:50", 0.17m, description: "ophalen badgelezer") };
        var day = new[]
        {
            selected[0],
            Performance(201, "09:10", "10:10", 1m, projectNumber: 100, description: null, bonNr: "0"),
            Performance(202, "11:00", "17:00", 6m, projectNumber: 100, description: null, bonNr: "0"),
        };
        var planning = new[]
        {
            Reservation(taskTypeId: 26, projectNumber: 501, from: "09:00", to: "10:00", subject: "Parker Hannifin"),
            Reservation(taskTypeId: 26, projectNumber: 502, from: "12:30", to: "18:30", subject: "Late job"),
        };
        var catalog = new KnownLocationCatalog(
        [
            new KnownLocationDefinition("The Belgian", "Meise", 50.984487, 4.300723, 120, "Slozenstraat 86"),
        ]);
        var gps = new StandbyGpsDayEvidence(
            "10",
            Day,
            true,
            false,
            "OBJ",
            "1-AAA",
            "Resolved",
            [
                new StandbyGpsTripEvidence("t1", At("07:54"), At("08:00"), 5m, 6, "9200 Dendermonde", "9300 Aalst", "OBJ", "1-AAA", 51.03m, 4.10m, 50.94m, 4.04m),
                new StandbyGpsTripEvidence("t2", At("08:02"), At("08:40"), 20m, 38, "9300 Aalst", "Slozenstraat 86, 1861 Meise", "OBJ", "1-AAA", 50.94m, 4.04m, 50.9845m, 4.3008m),
                new StandbyGpsTripEvidence("t3", At("08:48"), At("09:08"), 15m, 20, "Slozenstraat 86, 1861 Meise", "2850 Boom", "OBJ", "1-AAA", 50.9845m, 4.3008m, 51.09m, 4.37m),
                new StandbyGpsTripEvidence("t4", At("10:08"), At("10:57"), 20m, 40, "2850 Boom", "9300 Aalst", "OBJ", "1-AAA", 51.09m, 4.37m, 50.94m, 4.04m),
            ],
            "Plate");

        var full = PayrollProject300WorkbenchBuilder.BuildUnifiedDayTimeline(selected, day, planning, gps, knownLocations: catalog);
        var focused = PayrollProject300WorkbenchBuilder.BuildFocusedReviewContext(full, selected);

        Assert.Contains(focused.Before, item => item.Title.Contains("Dendermonde", StringComparison.Ordinal));
        Assert.Contains(focused.Before, item => item.Title.Contains("The Belgian", StringComparison.Ordinal));
        Assert.Contains(focused.Booking, item => item.IsSelected300);
        Assert.Contains(focused.After, item => item.Title.Contains("Vertrek", StringComparison.Ordinal) && item.Title.Contains("The Belgian", StringComparison.Ordinal));
        Assert.Contains(focused.After, item => item.Title.Contains("Boom", StringComparison.Ordinal));
        Assert.Contains(focused.After, item => item.Kind == PayrollProject300DayTimelineKind.Planning && item.Title.Contains("Parker", StringComparison.Ordinal));
        Assert.DoesNotContain(focused.After, item => item.Title.Contains("Late job", StringComparison.Ordinal));
        Assert.DoesNotContain(
            focused.Before.Concat(focused.Booking).Concat(focused.After),
            item => item.Kind == PayrollProject300DayTimelineKind.Performance && item.SortAt >= At("11:00"));
        Assert.True(focused.HasMoreThanFocused);
        Assert.Contains(focused.FullDay, item => item.Title.Contains("Late job", StringComparison.Ordinal));
        Assert.Contains("The Belgian", focused.ContextSummary!, StringComparison.Ordinal);
        Assert.Contains("Boom", focused.ContextSummary!, StringComparison.Ordinal);
        Assert.Contains("Parker", focused.ContextSummary!, StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceTimelineLabel_RejectsBonZero()
    {
        var withZero = Performance(1, "09:00", "10:00", 1m, projectNumber: 100, description: null, bonNr: "0");
        Assert.Equal("Project 100", PayrollProject300WorkbenchBuilder.BuildPerformanceTimelineLabel(withZero));

        var withDesc = Performance(2, "09:00", "10:00", 1m, projectNumber: 100, description: "Klantwerk", bonNr: "0");
        Assert.Equal("Klantwerk", PayrollProject300WorkbenchBuilder.BuildPerformanceTimelineLabel(withDesc));

        var bare = Performance(3, "09:00", "10:00", 1m, projectNumber: 300, description: null, bonNr: "0");
        Assert.Equal("Andere prestatie", PayrollProject300WorkbenchBuilder.BuildPerformanceTimelineLabel(bare));

        Assert.False(PayrollProject300WorkbenchBuilder.IsMeaningfulBonNr("0"));
        Assert.False(PayrollProject300WorkbenchBuilder.IsMeaningfulBonNr("00"));
        Assert.True(PayrollProject300WorkbenchBuilder.IsMeaningfulBonNr("26601932"));
        Assert.Equal("—", PayrollProject300WorkbenchBuilder.BuildProjectDisplayLabel(null, bonNr: "0"));
    }

    [Fact]
    public void DepartureDuringBooking_ShowsMinutesBeforeEnd()
    {
        var wording = PayrollProject300WorkbenchBuilder.FormatExactOverlapWording(
            At("08:48"),
            At("09:08"),
            At("08:40"),
            At("08:50"));
        // Full trip overlap classification may differ; chain builder uses dedicated departure wording.
        var trips = new[]
        {
            new StandbyGpsTripEvidence(
                "leave",
                At("08:48"),
                At("09:08"),
                10m,
                20,
                "Slozenstraat 86, 1861 Meise",
                "2850 Boom",
                "OBJ",
                "1-AAA",
                50.9845m,
                4.3008m,
                51.09m,
                4.37m),
        };
        var catalog = new KnownLocationCatalog(
        [
            new KnownLocationDefinition("The Belgian", "Meise", 50.984487, 4.300723, 120, null),
        ]);
        var chain = PayrollProject300WorkbenchBuilder.BuildGpsTripChainEntries(trips, At("08:40"), At("08:50"), catalog);
        var departure = Assert.Single(chain, item => item.Title.StartsWith("Vertrek", StringComparison.Ordinal));
        Assert.Contains("2 min vóór einde", departure.Subtitle!, StringComparison.Ordinal);
        Assert.NotNull(wording);
    }

    [Fact]
    public void FocusedContext_SummaryOmitsMissingFacts()
    {
        var selected = new[] { Performance(1, "10:00", "11:00", 1m) };
        var full = PayrollProject300WorkbenchBuilder.BuildUnifiedDayTimeline(selected, selected, [], gps: null);
        var focused = PayrollProject300WorkbenchBuilder.BuildFocusedReviewContext(full, selected);
        Assert.Contains("300 geboekt 10:00–11:00", focused.ContextSummary!, StringComparison.Ordinal);
        Assert.DoesNotContain("Aangekomen", focused.ContextSummary!, StringComparison.Ordinal);
        Assert.DoesNotContain("volgende bestemming", focused.ContextSummary!, StringComparison.Ordinal);
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
        bool isAbsence = false,
        string? bonNr = null)
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

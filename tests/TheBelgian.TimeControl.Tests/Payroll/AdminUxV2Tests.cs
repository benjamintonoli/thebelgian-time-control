using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class AdminUxV2Tests
{
    [Fact]
    public void Layout_RemovesPayrollShadow_AndShowsLooncontroleNav()
    {
        var layout = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Shared", "_Layout.cshtml"));
        Assert.Contains("Looncontrole", layout, StringComparison.Ordinal);
        Assert.Contains("Overzicht", layout, StringComparison.Ordinal);
        Assert.Contains("Beheer", layout, StringComparison.Ordinal);
        Assert.Contains("Voertuiginitialisatie", layout, StringComparison.Ordinal);
        Assert.Contains("Patronen", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(">Payroll shadow<", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Open reviews", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeDashboard_RendersOperationalMonthAndControlCards()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Index.cshtml"));
        Assert.Contains("TIME CONTROL", markup, StringComparison.Ordinal);
        Assert.Contains("Ga verder met looncontrole", markup, StringComparison.Ordinal);
        Assert.Contains("Volgende te beoordelen", markup, StringComparison.Ordinal);
        Assert.Contains("IPayrollControlCenterService", File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Index.cshtml.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("Open reviews", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerFleet", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IPayrollStandbyGpsSource", File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Index.cshtml.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void HomeDashboard_ZeroCountCardsRemainAccessible()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Index.cshtml"));
        Assert.Contains("CardToneClass", markup, StringComparison.Ordinal);
        Assert.Contains("CardSubtitle", markup, StringComparison.Ordinal);
        Assert.Contains("CardHref(card)", markup, StringComparison.Ordinal);
        Assert.Contains("tc-tone-muted", File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll", "PayrollNavUrls.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTechnicianDetail_ShowsExplainabilityAndHidesRawEnums()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "_MissingTechnicianDetail.cshtml"));
        Assert.Contains("MissingTechnicianAdminExplainability", markup, StringComparison.Ordinal);
        Assert.Contains("WhyIntro", markup, StringComparison.Ordinal);
        Assert.Contains("BookingStatusNl", markup, StringComparison.Ordinal);
        Assert.Contains("TimingSourcePrimary", markup, StringComparison.Ordinal);
        Assert.Contains("PeerRoleLabel", markup, StringComparison.Ordinal);
        Assert.Contains("Technische details", markup, StringComparison.Ordinal);
        Assert.Contains("WorkContinuityTitle", markup, StringComparison.Ordinal);
        Assert.Contains("Tijdsvoorstel op basis van", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatische correctie niet beschikbaar", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Geboekt: voorstel", markup, StringComparison.Ordinal);
        Assert.Contains("EvidenceClass:", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void GuidedDecisions_UseAdminFriendlyLabels()
    {
        Assert.Contains(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.MissingPerformance),
            item => item.Label == "Bestaande boeking nakijken");
        Assert.DoesNotContain(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.MissingPerformance),
            item => item.Label == "Huidige boeking lijkt fout");
        Assert.Contains(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.MissingPerformance),
            item => item.Label == "Andere uren voorstellen");
        Assert.Equal(
            "Voorstel klaar voor controle",
            PayrollGuidedDecisions.ActionabilityHint(PayrollReviewCaseActionability.ReadyProposal, null));
        Assert.DoesNotContain(
            "Automatische correctie niet beschikbaar",
            PayrollGuidedDecisions.ActionabilityHint(PayrollReviewCaseActionability.ReadyProposal, null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Explainability_OwnGpsVsPeer_AndHqWording()
    {
        var evidence =
            "planned=08:00-16:30; proposedInterval=07:55-14:46; siteArrival=07:55; siteDeparture=14:46; "
            + "intervalSource=gpsOwnVehicle; travelMode=SeparateVehicleProven; "
            + "excursion=WORK_HQ_VISIT; excursionInterval=12:22-13:42; "
            + "excursionAway=Slozenstraat 8, Meise; workContinuity=CONTINUOUS_SUPPORTED; "
            + "allocationReview=true; planningSubject=K388| Aquafin, test; "
            + "peers=[401#281607:08:10-14:45]; planningWindowConflict=#281602:15:15-15:50 proj=21540 (nonMaterialVsProposed);";

        var expl = MissingTechnicianAdminExplainability.Build(
            evidence,
            peerResourceId: "401",
            peerDisplayName: "Cano",
            peerStart: new TimeOnly(8, 10),
            peerEnd: new TimeOnly(14, 45),
            proposalStart: new TimeOnly(7, 55),
            proposalEnd: new TimeOnly(14, 46),
            proposalHours: 6.85m,
            projectId: "40167",
            bonNr: "26501760",
            mainTaskId: 9,
            travelMode: MissingTechnicianTravelMode.SeparateVehicleProven,
            canProposeCreate: true);

        Assert.Equal("Aquafin", expl.CustomerSiteLabel);
        Assert.Equal("Cano", expl.PeerDisplayName);
        Assert.Equal("08:10–14:45", expl.PeerInterval);
        Assert.Null(expl.LeadTechnicianName);
        Assert.Contains("eigen voertuig", expl.TimingSourcePrimary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planning + collega", expl.TimingSourceSupporting ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Track & Trace", expl.ProposalWhyLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("eigen Track & Trace", string.Join(' ', expl.WhyBullets), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HQ", expl.HqExcursionNl ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Meise", string.Join(' ', expl.WhyBullets), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Projecttoewijzing", expl.AllocationReviewNl ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Nog niet geregistreerd in Plenion", expl.BookingStatusNl);
        Assert.Contains("klaar voor controle", expl.ReadinessNl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15:15", expl.ExistingOtherBookingNl ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("PlanningPlusPeerPlusGps", expl.WorkContinuityNl, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTINUOUS_SUPPORTED", expl.WorkContinuityNl, StringComparison.Ordinal);
        Assert.Contains("Aankomst werf", string.Join('|', expl.TimelineLines), StringComparison.Ordinal);
    }

    [Fact]
    public void ActionConfirm_CreateRepeatsEvidence()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "ActionConfirm.cshtml"));
        Assert.Contains("Waarom", markup, StringComparison.Ordinal);
        Assert.Contains("Aan te maken", markup, StringComparison.Ordinal);
        Assert.Contains("Definitief aanmaken", markup, StringComparison.Ordinal);
        Assert.Contains("Nog niet geregistreerd in Plenion", markup, StringComparison.Ordinal);
        Assert.Contains("MissingTechnicianAdminExplainability.Build", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MonthStatus_UsesDutchAdminLabels()
    {
        Assert.Equal("In behandeling", PayrollReviewLabels.MonthStatus(PayrollShadowMonthStatus.InReview));
        Assert.Equal("Klaar voor finalisatie", PayrollReviewLabels.MonthStatus(PayrollShadowMonthStatus.ReadyForReview));
        Assert.Equal("Gefinaliseerd", PayrollReviewLabels.MonthStatus(PayrollShadowMonthStatus.Finalized));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}

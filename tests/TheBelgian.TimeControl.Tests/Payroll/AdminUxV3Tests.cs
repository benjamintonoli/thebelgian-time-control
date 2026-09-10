using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class AdminUxV3Tests
{
    [Fact]
    public void HomeCards_ResolveToAbsolutePayrollWorkbenchUrls()
    {
        Assert.Equal("/Admin/Payroll/Workbench", PayrollNavUrls.AbsolutePage("./Workbench"));
        Assert.Equal("/Admin/Payroll/MissingTechnicianWorkbench", PayrollNavUrls.AbsolutePage("./MissingTechnicianWorkbench"));
        Assert.Equal("/Admin/Payroll/StandbyWorkbench", PayrollNavUrls.AbsolutePage("./StandbyWorkbench"));
        Assert.Equal("/Admin/Payroll/OverlapWorkbench", PayrollNavUrls.AbsolutePage("./OverlapWorkbench"));
    }

    [Fact]
    public void HomeMarkup_UsesFullyClickableCardsWithResolvedHrefs()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Index.cshtml"));
        Assert.Contains("CardHref(card)", markup, StringComparison.Ordinal);
        Assert.Contains("PriorityHref(item)", markup, StringComparison.Ordinal);
        Assert.Contains("tc-home-card", markup, StringComparison.Ordinal);
        Assert.Contains("Ga verder met looncontrole", markup, StringComparison.Ordinal);
        Assert.Contains("Volgende te beoordelen", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"@card.NavigationPage\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTechnicianWorkbench_IsListDetailWithServerRenderedDetail()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "MissingTechnicianWorkbench.cshtml"));
        Assert.Contains("Ontbrekende prestaties", markup, StringComparison.Ordinal);
        Assert.Contains("PartialAsync(\"_MissingTechnicianDetail\"", markup, StringComparison.Ordinal);
        Assert.Contains("PayrollReviewQueueScope.FollowUp", markup, StringComparison.Ordinal);
        Assert.Contains("Te beoordelen", markup, StringComparison.Ordinal);
        Assert.Contains("Opvolging", markup, StringComparison.Ordinal);

        var code = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "MissingTechnicianWorkbench.cshtml.cs"));
        Assert.Contains("GetShellAsync", code, StringComparison.Ordinal);
        Assert.Contains("GetCoreDetailAsync", code, StringComparison.Ordinal); // selected detail handler
    }

    [Fact]
    public void MissingTechnicianDetail_ShowsNarrativeAndWhyByDefault()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "_MissingTechnicianDetail.cshtml"));
        Assert.Contains("Waarom dit voorstel?", markup, StringComparison.Ordinal);
        Assert.Contains("Voorgestelde prestatie", markup, StringComparison.Ordinal);
        Assert.Contains("Technische details", markup, StringComparison.Ordinal);
        Assert.Contains("Prestatie aanmaken", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Create controleren", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Explainability_IncludesNarrativeAndDayTimeline()
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
            "401",
            "Cano",
            new TimeOnly(8, 10),
            new TimeOnly(14, 45),
            new TimeOnly(7, 55),
            new TimeOnly(14, 46),
            6.85m,
            "40167",
            "26501760",
            9,
            MissingTechnicianTravelMode.SeparateVehicleProven,
            true);

        Assert.Contains("eigen Track & Trace", expl.NarrativeNl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cano", expl.NarrativeNl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("07:55-14:46", expl.NarrativeNl, StringComparison.Ordinal);
        Assert.Contains("Projectallocatie", expl.NarrativeNl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aankomst werf", string.Join('|', expl.TimelineLines), StringComparison.Ordinal);
        Assert.Contains("Daarna", string.Join('|', expl.TimelineLines), StringComparison.Ordinal);
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

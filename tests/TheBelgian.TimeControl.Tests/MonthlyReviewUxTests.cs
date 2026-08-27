using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.AdminReview;

namespace TheBelgian.TimeControl.Tests;

public sealed class MonthlyReviewUxTests
{
    [Fact]
    public void ReliableGpsCorrection_RoundsToExistingMinutePrecision()
    {
        var start = Boundary("Start", true, At(7, 5, 31), "Confirmed");
        var end = Boundary("End", true, At(12, 23, 17), "Probable");

        Assert.Equal(new TimeOnly(7, 6), DailyReviewDisplay.ReliableGpsCorrectionTime(start));
        Assert.Equal(new TimeOnly(12, 23), DailyReviewDisplay.ReliableGpsCorrectionTime(end));
    }

    [Theory]
    [InlineData("Unresolved")]
    [InlineData("AmbiguousVehicleAssignment")]
    [InlineData("InsufficientVehicleAssignment")]
    [InlineData("Review")]
    public void UnreliableBoundary_HasNoGpsQuickCorrection(string matcherStatus)
    {
        var boundary = Boundary("Start", false, At(7, 5, 31), matcherStatus);

        Assert.Null(DailyReviewDisplay.ReliableGpsCorrectionTime(boundary));
    }

    [Fact]
    public void PartialStartOnly_OffersOnlyStartCorrection()
    {
        var start = Boundary("Start", true, At(7, 5, 31), "Confirmed");
        var end = Boundary("End", false, At(12, 23, 17), "Unresolved");

        Assert.NotNull(DailyReviewDisplay.ReliableGpsCorrectionTime(start));
        Assert.Null(DailyReviewDisplay.ReliableGpsCorrectionTime(end));
    }

    [Fact]
    public void PartialEndOnly_OffersOnlyEndCorrection()
    {
        var start = Boundary("Start", false, At(7, 5, 31), "Unresolved");
        var end = Boundary("End", true, At(12, 23, 17), "Confirmed");

        Assert.Null(DailyReviewDisplay.ReliableGpsCorrectionTime(start));
        Assert.NotNull(DailyReviewDisplay.ReliableGpsCorrectionTime(end));
    }

    [Fact]
    public void Reviewer_UsesAuthenticatedUserThenConfiguredDefault()
    {
        Assert.Equal("Ada Admin", DailyReviewDisplay.ResolveReviewer(" Ada Admin ", "Benjamin Tonoli"));
        Assert.Equal("Benjamin Tonoli", DailyReviewDisplay.ResolveReviewer(null, " Benjamin Tonoli "));
    }

    [Fact]
    public void PreviousAndNext_ArePureCaseSelection()
    {
        var cases = new[] { ReviewCase("a"), ReviewCase("b"), ReviewCase("c") };

        Assert.Equal("a", DailyReviewDisplay.AdjacentCaseId(cases, "b", -1));
        Assert.Equal("c", DailyReviewDisplay.AdjacentCaseId(cases, "b", 1));
        Assert.All(cases, item => Assert.Equal(DailyReviewWorkflowStatus.Open, item.Decision.Status));
    }

    [Fact]
    public void SuccessfulDecision_SelectsCaseAtRemovedQueuePosition()
    {
        var remaining = new[] { ReviewCase("a"), ReviewCase("c"), ReviewCase("d") };

        Assert.Equal("c", DailyReviewDisplay.NextOpenCaseId(remaining, 1));
        Assert.Equal("d", DailyReviewDisplay.NextOpenCaseId(remaining, 9));
        Assert.Null(DailyReviewDisplay.NextOpenCaseId([], 0));
    }

    [Fact]
    public void RazorQueue_UsesFullCardGetLinksAndHasNoReviewerInput()
    {
        var root = FindRepoRoot();
        var page = File.ReadAllText(Path.Combine(root,
            "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "TimeControl", "Index.cshtml"));
        var css = File.ReadAllText(Path.Combine(root,
            "src", "TheBelgian.TimeControl.Web", "wwwroot", "css", "site.css"));

        Assert.Contains("class=\"queue-case-link\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-selectedCaseId=\"@item.CaseId\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-selectedCaseId=\"@previousCaseId\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-selectedCaseId=\"@nextCaseId\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-for=\"Reviewer\"", page, StringComparison.Ordinal);
        Assert.Contains(".queue-case { display: block;", css, StringComparison.Ordinal);
        Assert.Contains(".queue-case-link { display: block; width: 100%;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingEvidence_MapsBoundaryTripsAndCompleteDayContext()
    {
        var first = Boundary("Start", true, At(8, 0, 0), "Confirmed");
        var last = Boundary("End", true, At(16, 0, 0), "Confirmed");
        var json = """
            {
              "Trips": [
                {"TripId":"morning","Start":"2026-07-09T07:30:00+02:00","End":"2026-07-09T08:00:00+02:00","StartLocation":"The Belgian","EndLocation":"Eerste klant","StartLatitude":50.98,"StartLongitude":4.30,"EndLatitude":50.89,"EndLongitude":4.27},
                {"TripId":"evening","Start":"2026-07-09T16:00:00+02:00","End":"2026-07-09T16:30:00+02:00","StartLocation":"Laatste klant","EndLocation":"The Belgian","StartLatitude":50.89,"StartLongitude":4.27,"EndLatitude":50.98,"EndLongitude":4.30}
              ]
            }
            """;

        var context = DailyReviewTripContextMapper.Map(json, first, last);

        Assert.Equal("morning", context.TripBeforeFirstCustomer!.TripId);
        Assert.True(context.TripBeforeFirstCustomer.IsFirstBoundaryArrivalTrip);
        Assert.Equal("evening", context.TripAfterLastCustomer!.TripId);
        Assert.True(context.TripAfterLastCustomer.IsLastBoundaryDepartureTrip);
        Assert.Equal(2, context.DayTrips.Count);
        Assert.True(context.DayTrips[0].DistanceIsEstimated);
        Assert.InRange(context.DayTrips[0].DistanceKilometres!.Value, 9, 12);
    }

    [Fact]
    public void BoundaryAndDayInterpretation_ExplainDirectionWithoutChangingEvidence()
    {
        var reviewCase = ReviewCase("impact") with
        {
            First = Boundary("Start", true, At(8, 5, 0), "Confirmed") with
            {
                SignedDifferenceMinutes = 5,
            },
            Last = Boundary("End", true, At(15, 50, 0), "Confirmed") with
            {
                SignedDifferenceMinutes = 10,
            },
        };

        Assert.Contains("In voordeel", DailyReviewDisplay.BoundaryImpact(reviewCase.First));
        Assert.Contains("15 minuten meer", DailyReviewDisplay.DayInterpretation(reviewCase));
        Assert.Equal(15, reviewCase.ConfirmedPositiveMinutes);
    }

    [Fact]
    public void OneMinuteDifference_UsesSingularAndConsistentImpact()
    {
        var boundary = Boundary("Start", true, At(8, 1, 0), "Confirmed") with
        {
            SignedDifferenceMinutes = 0.8,
        };

        Assert.Contains("ongeveer 1 minuut", DailyReviewDisplay.Difference(boundary));
        Assert.StartsWith("In voordeel", DailyReviewDisplay.BoundaryImpact(boundary));
        Assert.Equal("impact-positive", DailyReviewDisplay.BoundaryImpactClass(boundary));
    }

    [Fact]
    public void RazorCaseDetail_ContainsCompactTripContextAndDayInsight()
    {
        var page = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "TimeControl", "Index.cshtml"));

        Assert.Contains("Rit vóór eerste klantprestatie", page, StringComparison.Ordinal);
        Assert.Contains("Rit na laatste klantprestatie", page, StringComparison.Ordinal);
        Assert.Contains("Alle ritten van deze dag bekijken", page, StringComparison.Ordinal);
        Assert.Contains("Bevestigd positief", page, StringComparison.Ordinal);
        Assert.Contains("Start aanpassen naar GPS", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-for=\"Reviewer\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedProposal_ShowsExecuteButtonAndAvailabilityMessage()
    {
        var page = ReadPage();

        Assert.Contains("Correctievoorstel", page, StringComparison.Ordinal);
        Assert.Contains("Correctie uitvoeren in Plenion", page, StringComparison.Ordinal);
        Assert.Contains("disabled=\"@(!Model.CorrectionAvailability.CanExecute)\"", page, StringComparison.Ordinal);
        Assert.Contains("@Model.CorrectionAvailability.Message", page, StringComparison.Ordinal);
    }

    [Fact]
    public void NoProposal_HasNoExecuteAction()
    {
        var page = ReadPage();
        var proposalGuard = page.IndexOf("@if (Model.CorrectionProposal is { } proposal)",
            StringComparison.Ordinal);
        var executeAction = page.IndexOf("asp-page-handler=\"ExecuteCorrection\"",
            StringComparison.Ordinal);

        Assert.True(proposalGuard >= 0);
        Assert.True(executeAction > proposalGuard);
        Assert.Equal(1, page.Split("asp-page-handler=\"ExecuteCorrection\"").Length - 1);
    }

    [Fact]
    public void QuickGps_OnlyFillsProposalAndNeverExecutesCorrection()
    {
        var page = ReadPage();

        Assert.Contains("gps-correction", page, StringComparison.Ordinal);
        Assert.Contains("Snelle GPS-correcties vullen alleen het voorstel in", page, StringComparison.Ordinal);
        Assert.Contains("type=\"button\" class=\"btn btn-sm btn-outline-primary gps-correction\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page-handler=\"ExecuteCorrection\" class=\"gps-correction\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectionExecution_RequiresExplicitConfirmation()
    {
        var page = ReadPage();
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src", "TheBelgian.TimeControl.Web", "wwwroot", "js", "site.js"));

        Assert.Contains("data-confirm=", page, StringComparison.Ordinal);
        Assert.Contains("correction-execute-form", page, StringComparison.Ordinal);
        Assert.Contains("window.confirm(message)", script, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"ConfirmCorrectionExecution\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutedProposal_ShowsOldAndNewValuesActorAndTimestamp()
    {
        var page = ReadPage();

        Assert.Contains("ongewijzigd", page, StringComparison.Ordinal);
        Assert.Contains("✓ Correctie uitgevoerd in Plenion", page, StringComparison.Ordinal);
        Assert.Contains("Start aangepast:", page, StringComparison.Ordinal);
        Assert.Contains("Einde aangepast:", page, StringComparison.Ordinal);
        Assert.Contains("Uitgevoerd door:", page, StringComparison.Ordinal);
        Assert.Contains("Uitgevoerd op:", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictProposal_ShowsNeedsReReview()
    {
        var page = ReadPage();

        Assert.Contains("NeedsReReview / Conflict", page, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectionAvailability_DisabledMessage_IsExplicit()
    {
        var availability = new CorrectionExecutionAvailability(
            false, false, "Plenion-correcties zijn momenteel uitgeschakeld.");
        Assert.False(availability.CanExecute);
        Assert.Contains("uitgeschakeld", availability.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorrectionAvailability_EnabledAndReachable_CanExecute()
    {
        var availability = new CorrectionExecutionAvailability(true, true, "ok");
        Assert.True(availability.CanExecute);
    }

    private static DailyReviewBoundaryEvidence Boundary(
        string side, bool reliable, DateTimeOffset? gps, string matcherStatus) => new(
        side, 1, "Klant", "Adres", At(8, 0, 0), gps, 1, reliable,
        reliable ? "ExactSite" : "Review", matcherStatus, 90, 10, 30, "visit", "test");

    private static DailyReviewCase ReviewCase(string id)
    {
        var first = Boundary("Start", true, At(8, 1, 0), "Confirmed");
        var last = Boundary("End", true, At(16, 0, 0), "Confirmed");
        return new DailyReviewCase(id, id, id, new DateOnly(2026, 7, 1), first, last,
            DailyReviewEvidenceLevel.Complete, "Reliable", "test", At(0, 0, 0), "{}",
            new DailyReviewDecision(DailyReviewWorkflowStatus.Open, null, null, null, null, null, null));
    }

    private static DateTimeOffset At(int hour, int minute, int second) =>
        new(2026, 7, 9, hour, minute, second, TimeSpan.FromHours(2));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TheBelgian.TimeControl.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root niet gevonden.");
    }

    private static string ReadPage() => File.ReadAllText(Path.Combine(FindRepoRoot(),
        "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "TimeControl", "Index.cshtml"));
}

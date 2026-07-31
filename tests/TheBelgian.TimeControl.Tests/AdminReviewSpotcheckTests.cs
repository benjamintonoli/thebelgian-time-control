using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.AdminReview;

namespace TheBelgian.TimeControl.Tests;

public sealed class AdminReviewSpotcheckTests
{
    [Theory]
    [InlineData(0, SpotcheckPriorityTier.Informational)]
    [InlineData(3, SpotcheckPriorityTier.Informational)]
    [InlineData(5, SpotcheckPriorityTier.PatternRelevant)]
    [InlineData(14, SpotcheckPriorityTier.PatternRelevant)]
    [InlineData(15, SpotcheckPriorityTier.IndividualException)]
    [InlineData(29, SpotcheckPriorityTier.IndividualException)]
    [InlineData(30, SpotcheckPriorityTier.High)]
    public void Priority_UsesDocumentedBands(int deviation, SpotcheckPriorityTier expected)
    {
        Assert.Equal(expected, SpotcheckPriorityCalculator.FromDeviationMinutes(deviation));
    }

    [Fact]
    public void MatcherAcceptance_StartsAsPending()
    {
        Assert.Equal(
            AdminReviewStatus.Pending,
            AdminReviewDecisionRules.InitialReviewStatus(matcherProposedAcceptance: true));
        Assert.Equal(
            AdminReviewStatus.Pending,
            AdminReviewDecisionRules.InitialReviewStatus(matcherProposedAcceptance: false));
    }

    [Fact]
    public void Confirmation_RequiresReviewer()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdminReviewDecisionRules.Validate(AdminReviewStatus.Confirmed, reviewer: " ", comment: null));
        Assert.Contains("reviewer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejection_RequiresReason()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdminReviewDecisionRules.Validate(AdminReviewStatus.Rejected, reviewer: "Ada", comment: null));
        Assert.Contains("reden", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingDecision_CannotBePersisted()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdminReviewDecisionRules.Validate(AdminReviewStatus.Pending, "Ada", null));
    }

    [Fact]
    public void RecurringSmallAdvantage_DetectedPerTechnician()
    {
        var rows = new List<(string, int, int)>();
        for (var i = 0; i < 3; i++)
        {
            rows.Add(("Jasper", 2, 0));
        }

        rows.Add(("Filip", 2, 0));
        var recurring = SpotcheckPriorityCalculator.DetectRecurringSmallAdvantageTechnicians(rows);
        Assert.Contains("Jasper", recurring);
        Assert.DoesNotContain("Filip", recurring);
    }

    [Fact]
    public void FilterAndSort_OrdersByDeviationBandsThenRecurring()
    {
        var cases = new[]
        {
            Case(1, 10, false, "Unresolved"),
            Case(2, 35, false, "Probable"),
            Case(3, 16, true, "Ambiguous"),
            Case(4, 2, true, "Probable"),
        };

        var sorted = SpotcheckPriorityCalculator.ApplyFilterAndSort(
            cases,
            new AdminReviewFilter());

        Assert.Equal(new long[] { 2, 3, 4, 1 }, sorted.Select(item => item.PerformanceId).ToArray());
    }

    [Fact]
    public void Filter_ProposedMatchesOnly_And_AmbiguousUnresolved()
    {
        var cases = new[]
        {
            Case(1, 40, false, "Probable", proposed: true),
            Case(2, 40, false, "Ambiguous", proposed: false),
            Case(3, 40, false, "Unresolved", proposed: false),
        };

        var proposed = SpotcheckPriorityCalculator.ApplyFilterAndSort(
            cases,
            new AdminReviewFilter(ProposedMatchesOnly: true));
        Assert.Equal(new long[] { 1 }, proposed.Select(item => item.PerformanceId).ToArray());

        var amb = SpotcheckPriorityCalculator.ApplyFilterAndSort(
            cases,
            new AdminReviewFilter(AmbiguousOrUnresolvedOnly: true));
        Assert.Equal(new long[] { 2, 3 }, amb.Select(item => item.PerformanceId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void AuditRow_KeepsOriginalMatcherOutcome()
    {
        var audit = new AdminReviewDecisionAudit
        {
            PerformanceId = 42,
            OriginalMatcherDecision = "RecoveredProbable",
            ProposedVisitCandidateId = "a/b",
            ProposedVisitSourceStopIdsJson = "[\"a\",\"b\"]",
            AdminDecision = nameof(AdminReviewStatus.Confirmed),
            ChosenVisitCandidateId = "c/d",
            ChosenVisitSourceStopIdsJson = "[\"c\",\"d\"]",
            Comment = "andere kandidaat gekozen",
            Reviewer = "Ada",
            DecidedAt = DateTimeOffset.UtcNow,
            MatcherCommit = "abc",
            ConfigurationHashSha256 = "hash",
        };

        Assert.Equal("RecoveredProbable", audit.OriginalMatcherDecision);
        Assert.Equal("a/b", audit.ProposedVisitCandidateId);
        Assert.Equal("c/d", audit.ChosenVisitCandidateId);
        Assert.Equal(nameof(AdminReviewStatus.Confirmed), audit.AdminDecision);
    }

    [Fact]
    public void AdminReview_DoesNotLoadLockedHoldout()
    {
        Assert.False(AdminReviewService.LoadsLockedHoldout);
        var source = File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "src",
                "TheBelgian.TimeControl.Infrastructure",
                "AdminReview",
                "AdminReviewService.cs"));
        Assert.DoesNotContain("location-matching-holdout.json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluate-locked-holdout", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LockedHoldoutEvaluation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MatcherUsagePolicy_IsHumanReviewRequired_NoAutoAccept()
    {
        Assert.Equal(MatcherUsageMode.HumanReviewRequired, MatcherUsagePolicy.CurrentMode);
        Assert.False(MatcherUsagePolicy.AutomaticAcceptanceAllowed);
        Assert.Equal("NO-GO", MatcherUsagePolicy.HoldoutDecision);
    }

    [Fact]
    public void NoPlenionWritebackSurface_InAdminReview()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "src",
                "TheBelgian.TimeControl.Infrastructure",
                "AdminReview",
                "AdminReviewService.cs"));
        Assert.DoesNotContain("INSERT INTO", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WriteBack", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", source, StringComparison.OrdinalIgnoreCase);
    }

    private static AdminReviewCase Case(
        long id,
        int deviation,
        bool recurring,
        string matcherStatus,
        bool proposed = false) =>
        new(
            PerformanceId: id,
            Date: new DateOnly(2026, 7, 1),
            Technician: "Tech",
            PerformanceStart: DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture),
            PerformanceEnd: DateTimeOffset.Parse("2026-07-01T10:00:00+02:00", CultureInfo.InvariantCulture),
            PlenionAddress: "x",
            Lacleunik: null,
            ProjectOrBonContext: null,
            PreviousPerformance: null,
            NextPerformance: null,
            MatcherStatus: matcherStatus,
            MatchReason: "test",
            MatcherProposedAcceptance: proposed || matcherStatus is "Probable" or "Confirmed" or "RecoveredProbable",
            ProposedVisit: null,
            CandidateVisits: [],
            GeocodeQuality: GeocodeQualityClass.PartialAddress,
            MaxDeviationMinutes: deviation,
            Priority: SpotcheckPriorityCalculator.FromDeviationMinutes(deviation),
            RecurringSmallAdvantage: recurring,
            ReviewStatus: AdminReviewStatus.Pending,
            LatestReviewer: null,
            LatestComment: null,
            MatcherCommit: "test",
            ConfigurationHashSha256: "hash");

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

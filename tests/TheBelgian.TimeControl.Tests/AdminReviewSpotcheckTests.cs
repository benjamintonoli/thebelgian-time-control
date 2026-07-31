using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests;

public sealed class AdminReviewSpotcheckTests
{
    [Theory]
    [InlineData(0, SpotcheckPriorityTier.Informational)]
    [InlineData(5, SpotcheckPriorityTier.SmallDeviation)]
    [InlineData(15, SpotcheckPriorityTier.IndividualException)]
    [InlineData(30, SpotcheckPriorityTier.HighPriority)]
    public void Priority_UsesDocumentedBands(int deviation, SpotcheckPriorityTier expected)
    {
        Assert.Equal(expected, SpotcheckPriorityCalculator.FromDeviationMinutes(deviation));
    }

    [Fact]
    public void TabCounts_SeparateExceptionsFromSmallDeviations()
    {
        var cases = new[]
        {
            SpotcheckPriorityCalculator.WithDerivedFields(Proposed(1, 40), false),
            SpotcheckPriorityCalculator.WithDerivedFields(Proposed(2, 8), false),
            SpotcheckPriorityCalculator.WithDerivedFields(Ambiguous(3), false),
            SpotcheckPriorityCalculator.WithDerivedFields(UnresolvedWithoutVisit(4), false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(5, 40, AdminReviewStatus.Confirmed), false),
        };
        var counts = SpotcheckPriorityCalculator.CountCategories(cases);
        Assert.Equal(2, counts.OpenOutstanding);
        Assert.Equal(1, counts.Exceptions);
        Assert.Equal(1, counts.SmallDeviation);
        Assert.Equal(1, counts.MatchUncertainty);
        Assert.Equal(1, counts.DataQuality);
        Assert.Equal(1, counts.Completed);
    }

    [Fact]
    public void DefaultTab_ShowsOnlyExceptions()
    {
        var cases = new[]
        {
            SpotcheckPriorityCalculator.WithDerivedFields(Proposed(1, 40), false),
            SpotcheckPriorityCalculator.WithDerivedFields(Proposed(2, 8), false),
            SpotcheckPriorityCalculator.WithDerivedFields(Proposed(3, 2), false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(4, 40, AdminReviewStatus.Confirmed), false),
        };
        var result = SpotcheckPriorityCalculator.ApplyFilterAndPage(
            cases,
            SpotcheckPriorityCalculator.DefaultWorklistFilter(),
            4,
            0,
            4);
        Assert.Equal(ReviewWorkTab.Exceptions, SpotcheckPriorityCalculator.NormalizeFilter(new AdminReviewFilter()).Tab);
        Assert.All(result.Items, item => Assert.Equal(ReviewWorkCategory.ActionableDeviation, item.Category));
        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalMatching);
        Assert.DoesNotContain(result.Items, item => item.Category == ReviewWorkCategory.SmallDeviation);
        Assert.DoesNotContain(result.Items, item => item.Category == ReviewWorkCategory.Informational);
        Assert.DoesNotContain(result.Items, item => item.Category == ReviewWorkCategory.Completed);
    }

    [Fact]
    public void ImpactAndEvidence_AreIndependent()
    {
        var highRecovered = SpotcheckPriorityCalculator.WithDerivedFields(
            Proposed(1, 42, status: AdminReviewStatus.Pending, matcherStatus: "RecoveredProbable"),
            false);
        Assert.Equal(SpotcheckPriorityTier.HighPriority, highRecovered.Priority);
        Assert.Equal(EvidenceStrength.ProbableVisit, AdminReviewDisplay.Evidence(highRecovered.MatcherStatus));
        Assert.Equal("Hoog", AdminReviewDisplay.Impact(highRecovered.Priority));
        Assert.Equal("Waarschijnlijk bezoek", AdminReviewDisplay.EvidenceLabel(highRecovered.MatcherStatus));

        var highStrong = SpotcheckPriorityCalculator.WithDerivedFields(
            Proposed(2, 42, matcherStatus: "Probable"),
            false);
        Assert.Equal(EvidenceStrength.StrongProposal, AdminReviewDisplay.Evidence(highStrong.MatcherStatus));
        Assert.Equal("Sterk voorstel", AdminReviewDisplay.EvidenceLabel(highStrong.MatcherStatus));
    }

    [Fact]
    public void VisitPresentation_IncludesDistanceAndOverlap()
    {
        var visit = new ReviewVisitCandidate(
            "s1",
            ["s1"],
            "Watertorenstraat 12",
            DateTimeOffset.Parse("2026-07-01T12:06:00+02:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-01T13:04:00+02:00", CultureInfo.InvariantCulture),
            76,
            20,
            30,
            6,
            4,
            "PartialAddress");
        Assert.Equal("Watertorenstraat 12", AdminReviewDisplay.VisitAddressLine(visit));
        Assert.Contains("12:06–13:04", AdminReviewDisplay.VisitMetricsLine(visit), StringComparison.Ordinal);
        Assert.Contains("76 m", AdminReviewDisplay.VisitMetricsLine(visit), StringComparison.Ordinal);
        Assert.Contains("30% overlap", AdminReviewDisplay.VisitMetricsLine(visit), StringComparison.Ordinal);
        Assert.Contains("gedeeltelijke dekking", AdminReviewDisplay.VisitMetricsLine(visit), StringComparison.Ordinal);
    }

    [Fact]
    public void DetailPath_PointsToPerformanceRoute()
    {
        var item = SpotcheckPriorityCalculator.WithDerivedFields(Proposed(279688, 40), false);
        Assert.Equal("/Admin/Reviews/279688", item.DetailPath);
    }

    [Fact]
    public void UnresolvedWithoutVisit_HasNullDeviations_NeverHighPriority()
    {
        var derived = SpotcheckPriorityCalculator.WithDerivedFields(UnresolvedWithoutVisit(1), true);
        Assert.Null(derived.Matcher.StartDeviationMinutes);
        Assert.Null(derived.Priority);
        Assert.Equal(ReviewWorkCategory.DataQuality, derived.Category);
        Assert.False(derived.HasRecurringConfirmedPattern);
        Assert.Equal(EvidenceStrength.NoReliableMatch, AdminReviewDisplay.Evidence(derived.MatcherStatus));
    }

    [Fact]
    public void Rejection_RequiresReason_AlternateRequiresComment()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdminReviewDecisionRules.Validate(AdminReviewStatus.Rejected, "Ada", null));
        Assert.Throws<InvalidOperationException>(() =>
            AdminReviewDecisionRules.Validate(
                AdminReviewStatus.Confirmed,
                "Ada",
                null,
                "a/b",
                "c/d"));
    }

    [Fact]
    public async Task Decisions_AreAppendOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TimeControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new TimeControlDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var repository = new AdminReviewDecisionRepository(new TestDbContextFactory(options));
        await repository.AppendAsync(
            new AdminReviewDecisionAudit
            {
                PerformanceId = 99,
                OriginalMatcherStatus = "Probable",
                ProposedVisitCandidateId = "a",
                Decision = nameof(AdminReviewStatus.Confirmed),
                ChosenVisitCandidateId = "a",
                Reviewer = "Ada",
                DecidedAt = DateTimeOffset.Parse("2026-07-01T10:00:00Z", CultureInfo.InvariantCulture),
                MatcherCommit = "c1",
                ConfigurationHash = "h1",
            },
            CancellationToken.None);
        await repository.AppendAsync(
            new AdminReviewDecisionAudit
            {
                PerformanceId = 99,
                OriginalMatcherStatus = "Probable",
                ProposedVisitCandidateId = "a",
                Decision = nameof(AdminReviewStatus.Rejected),
                ReasonOrComment = "toch niet",
                Reviewer = "Bea",
                DecidedAt = DateTimeOffset.Parse("2026-07-01T11:00:00Z", CultureInfo.InvariantCulture),
                MatcherCommit = "c1",
                ConfigurationHash = "h1",
            },
            CancellationToken.None);

        var trail = await repository.ListForPerformanceAsync(99, CancellationToken.None);
        Assert.Equal(2, trail.Count);
        Assert.Equal("Probable", trail[0].OriginalMatcherStatus);
        Assert.Equal("Probable", trail[1].OriginalMatcherStatus);
    }

    [Fact]
    public void Banner_IsShortAndHumanFacing()
    {
        Assert.Equal("Menselijke bevestiging verplicht", MatcherUsagePolicy.BannerTitle);
        Assert.Equal(
            "De tool doet voorstellen en voert geen automatische correcties uit.",
            MatcherUsagePolicy.BannerBody);
    }

    [Fact]
    public void DevelopmentNavigation_IsGatedInLayout()
    {
        var layout = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web", "Pages", "Shared", "_Layout.cshtml"));
        Assert.Contains("HostEnvironment.IsDevelopment()", layout, StringComparison.Ordinal);
        Assert.Contains("Development", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Exceptions/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Pilot/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Admin/Reviews/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Admin/Patterns/Index\"", layout, StringComparison.Ordinal);
        var developmentBlockStart = layout.IndexOf("HostEnvironment.IsDevelopment()", StringComparison.Ordinal);
        var exceptionsInDev = layout.IndexOf("asp-page=\"/Exceptions/Index\"", developmentBlockStart, StringComparison.Ordinal);
        Assert.True(exceptionsInDev > developmentBlockStart);
    }

    [Fact]
    public void AdminReview_DoesNotLoadLockedHoldout_NoAi_NoWriteback()
    {
        Assert.False(OfflineReviewCaseProvider.LoadsLockedHoldoutFlag);
        Assert.False(MatcherUsagePolicy.PlenionWritebackAllowed);
        Assert.False(LiveReviewCaseProvider.IsEnabled);
        var offline = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "AdminReview", "OfflineReviewCaseProvider.cs"));
        Assert.DoesNotContain("location-matching-holdout.json", offline, StringComparison.Ordinal);
        var di = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "DependencyInjection.cs"));
        Assert.DoesNotContain("OpenAI", di, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DeterministicReviewExplanationService", di, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineProvider_DeduplicatesPerformanceIds()
    {
        var env = new TestHostEnvironment(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web"));
        var provider = new OfflineReviewCaseProvider(
            env,
            Options.Create(new AdaptiveLocationMatchingOptions()));
        var cases = await provider.GetCasesAsync(CancellationToken.None);
        Assert.Equal(cases.Count, provider.UniqueCaseCount);
        Assert.Equal(provider.RawCaseCount - provider.UniqueCaseCount, provider.DuplicatesRemoved);
        Assert.Equal(cases.Count, cases.Select(item => item.PerformanceId).Distinct().Count());
    }

    [Fact]
    public void Pattern_RequiresThreeConfirmed()
    {
        var cases = new[]
        {
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(1, 8, AdminReviewStatus.Confirmed, technician: "Ada", date: new DateOnly(2026, 7, 1)), false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(2, 9, AdminReviewStatus.Confirmed, technician: "Ada", date: new DateOnly(2026, 7, 5)), false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(3, 10, AdminReviewStatus.Confirmed, technician: "Ada", date: new DateOnly(2026, 7, 10)), false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(4, 12, AdminReviewStatus.Pending, technician: "Ada", date: new DateOnly(2026, 7, 12)), false),
        };
        var ids = RecurringConfirmedPatternDetector.DetectPerformanceIds(cases);
        Assert.Contains(1, ids);
        Assert.Contains(2, ids);
        Assert.Contains(3, ids);
        Assert.DoesNotContain(4, ids);
    }

    private static ReviewCase UnresolvedWithoutVisit(long id, string technician = "Tech")
    {
        var start = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse("2026-07-01T10:00:00+02:00", CultureInfo.InvariantCulture);
        return new ReviewCase(
            new SourceEvidence(id, new DateOnly(2026, 7, 1), technician, start, end, "x", null, null, null, null, null, null),
            new MatcherAssessment("Unresolved", false, null, [], "test", GeocodeQualityClass.PartialAddress, null, null, null, "test", "hash"),
            new AdminDecision(AdminReviewStatus.Pending),
            null,
            ReviewWorkCategory.DataQuality,
            false,
            ["development"]);
    }

    private static ReviewCase Ambiguous(long id)
    {
        var start = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture);
        var end = start.AddHours(1);
        var visit = new ReviewVisitCandidate("s1", ["s1"], "A", start, end, 40, 10, 20, 0, 0, "PartialAddress");
        return new ReviewCase(
            new SourceEvidence(id, new DateOnly(2026, 7, 1), "Tech", start, end, "x", null, null, null, null, null, null),
            new MatcherAssessment("Ambiguous", false, null, [visit], "amb", GeocodeQualityClass.PartialAddress, null, null, null, "t", "h"),
            new AdminDecision(AdminReviewStatus.Pending),
            null,
            ReviewWorkCategory.MatchUncertainty,
            false,
            ["development"]);
    }

    private static ReviewCase Proposed(
        long id,
        int startDeviation,
        AdminReviewStatus status = AdminReviewStatus.Pending,
        string technician = "Tech",
        DateOnly? date = null,
        string matcherStatus = "Probable")
    {
        var day = date ?? new DateOnly(2026, 7, 1);
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(2));
        var end = start.AddHours(1);
        var visit = new ReviewVisitCandidate(
            "s1",
            ["s1"],
            "Bezoekstraat 1",
            start.AddMinutes(startDeviation),
            end,
            40,
            50,
            80,
            startDeviation,
            0,
            "PartialAddress");
        return new ReviewCase(
            new SourceEvidence(id, day, technician, start, end, "Plenionstraat 1", null, null, null, null, null, null),
            new MatcherAssessment(
                matcherStatus,
                true,
                visit,
                [visit],
                "voorstel",
                GeocodeQualityClass.PartialAddress,
                startDeviation,
                0,
                Math.Abs(startDeviation),
                "test",
                "hash"),
            new AdminDecision(status),
            SpotcheckPriorityCalculator.FromDeviationMinutes(Math.Abs(startDeviation)),
            ReviewWorkCategory.ActionableDeviation,
            false,
            ["development"]);
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

    private sealed class TestDbContextFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);

        public ValueTask<TimeControlDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

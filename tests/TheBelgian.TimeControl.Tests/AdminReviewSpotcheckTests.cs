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
    [InlineData(3, SpotcheckPriorityTier.Informational)]
    [InlineData(5, SpotcheckPriorityTier.SmallDeviation)]
    [InlineData(14, SpotcheckPriorityTier.SmallDeviation)]
    [InlineData(15, SpotcheckPriorityTier.IndividualException)]
    [InlineData(29, SpotcheckPriorityTier.IndividualException)]
    [InlineData(30, SpotcheckPriorityTier.HighPriority)]
    public void Priority_UsesDocumentedBands(int deviation, SpotcheckPriorityTier expected)
    {
        Assert.Equal(expected, SpotcheckPriorityCalculator.FromDeviationMinutes(deviation));
    }

    [Fact]
    public void Priority_NullWithoutVisitAnchor()
    {
        Assert.Null(SpotcheckPriorityCalculator.FromDeviationMinutes(null));
    }

    [Fact]
    public void UnresolvedWithoutVisit_HasNullDeviations_NeverHighPriority()
    {
        var item = UnresolvedWithoutVisit(1);
        var derived = SpotcheckPriorityCalculator.WithDerivedFields(item, recurringPattern: true);
        Assert.Null(derived.Matcher.StartDeviationMinutes);
        Assert.Null(derived.Matcher.EndDeviationMinutes);
        Assert.Null(derived.Matcher.MaxDeviationMinutes);
        Assert.Null(derived.Priority);
        Assert.Equal(ReviewWorkCategory.DataQuality, derived.Category);
        Assert.False(derived.HasRecurringConfirmedPattern);
    }

    [Fact]
    public void Informational_And_Completed_NotInDefaultList()
    {
        var cases = new[]
        {
            Proposed(1, 40, AdminReviewStatus.Pending),
            Proposed(2, 2, AdminReviewStatus.Pending),
            Proposed(3, 40, AdminReviewStatus.Confirmed),
            UnresolvedWithoutVisit(4),
        };
        var derived = cases
            .Select(item => SpotcheckPriorityCalculator.WithDerivedFields(item, false))
            .ToArray();
        Assert.Equal(ReviewWorkCategory.Informational, derived[1].Category);
        Assert.Equal(ReviewWorkCategory.Completed, derived[2].Category);

        var result = SpotcheckPriorityCalculator.ApplyFilterAndPage(
            derived,
            SpotcheckPriorityCalculator.DefaultWorklistFilter(),
            uniqueCaseCount: 4,
            duplicatesRemoved: 0,
            rawCaseCount: 4);

        Assert.All(result.Items, item => Assert.Equal(ReviewWorkCategory.ActionableDeviation, item.Category));
        Assert.DoesNotContain(result.Items, item => item.Category == ReviewWorkCategory.Informational);
        Assert.DoesNotContain(result.Items, item => item.Category == ReviewWorkCategory.Completed);
        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalMatching);
    }

    [Fact]
    public void DefaultFilter_PagesAt25_NewestFirst()
    {
        var cases = Enumerable.Range(1, 30)
            .Select(i => SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(i, 40, AdminReviewStatus.Pending, date: new DateOnly(2026, 7, i <= 31 ? i : 1)),
                false))
            .ToArray();

        var page1 = SpotcheckPriorityCalculator.ApplyFilterAndPage(
            cases,
            SpotcheckPriorityCalculator.DefaultWorklistFilter(page: 1),
            30,
            0,
            30);
        Assert.Equal(25, page1.Items.Count);
        Assert.Equal(30, page1.TotalMatching);
        Assert.True(page1.Items[0].Date >= page1.Items[^1].Date);

        var page2 = SpotcheckPriorityCalculator.ApplyFilterAndPage(
            cases,
            SpotcheckPriorityCalculator.DefaultWorklistFilter(page: 2),
            30,
            0,
            30);
        Assert.Equal(5, page2.Items.Count);
    }

    [Fact]
    public void Pattern_RequiresThreeConfirmed_SameDirectionAndKind()
    {
        var cases = new[]
        {
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(1, 8, AdminReviewStatus.Confirmed, technician: "Ada", date: new DateOnly(2026, 7, 1)),
                false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(2, 9, AdminReviewStatus.Confirmed, technician: "Ada", date: new DateOnly(2026, 7, 5)),
                false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(3, 10, AdminReviewStatus.Confirmed, technician: "Ada", date: new DateOnly(2026, 7, 10)),
                false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(4, 12, AdminReviewStatus.Pending, technician: "Ada", date: new DateOnly(2026, 7, 12)),
                false),
            SpotcheckPriorityCalculator.WithDerivedFields(
                UnresolvedWithoutVisit(5, technician: "Ada"),
                false),
        };

        var ids = RecurringConfirmedPatternDetector.DetectPerformanceIds(cases);
        Assert.Contains(1, ids);
        Assert.Contains(2, ids);
        Assert.Contains(3, ids);
        Assert.DoesNotContain(4, ids);
        Assert.DoesNotContain(5, ids);

        var pending = SpotcheckPriorityCalculator.WithDerivedFields(cases[3], recurringPattern: ids.Contains(4));
        Assert.False(pending.HasRecurringConfirmedPattern);
        var unresolved = SpotcheckPriorityCalculator.WithDerivedFields(cases[4], recurringPattern: true);
        Assert.False(unresolved.HasRecurringConfirmedPattern);
    }

    [Fact]
    public void Pattern_NotFormedFromPendingOrUnresolvedAlone()
    {
        var cases = Enumerable.Range(1, 5)
            .Select(i => SpotcheckPriorityCalculator.WithDerivedFields(
                Proposed(i, 8, AdminReviewStatus.Pending, technician: "Bea", date: new DateOnly(2026, 7, i)),
                false))
            .Append(SpotcheckPriorityCalculator.WithDerivedFields(
                UnresolvedWithoutVisit(99, technician: "Bea"),
                false))
            .ToArray();
        Assert.Empty(RecurringConfirmedPatternDetector.DetectPerformanceIds(cases));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "op tijd")]
    [InlineData(31, "31 min later")]
    [InlineData(-18, "18 min vroeger")]
    public void DeviationTexts_AreCorrect(int? minutes, string expected)
    {
        Assert.Equal(expected, AdminReviewDisplay.Deviation(minutes));
    }

    [Fact]
    public void AdminTexts_ReplaceInternalStatuses()
    {
        Assert.Equal("Waarschijnlijk bezoek", AdminReviewDisplay.MatcherStatus("RecoveredProbable"));
        Assert.Equal("Voorgesteld bezoek", AdminReviewDisplay.MatcherStatus("Probable"));
        Assert.Equal("Meerdere mogelijke bezoeken", AdminReviewDisplay.MatcherStatus("Ambiguous"));
        Assert.Equal("Geen betrouwbare match", AdminReviewDisplay.MatcherStatus("Unresolved"));
        Assert.Equal("Menselijke bevestiging verplicht", MatcherUsagePolicy.BannerTitle);
    }

    [Fact]
    public void NewCases_StartAsPending()
    {
        Assert.Equal(AdminReviewStatus.Pending, AdminReviewDecisionRules.InitialReviewStatus());
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
    public void AlternateCandidate_RequiresComment()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdminReviewDecisionRules.Validate(
                AdminReviewStatus.Confirmed,
                reviewer: "Ada",
                comment: null,
                proposedVisitCandidateId: "a/b",
                chosenVisitCandidateId: "c/d"));
        Assert.Contains("andere kandidaat", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        var factory = new TestDbContextFactory(options);
        var repository = new AdminReviewDecisionRepository(factory);
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
    public void AdminReview_DoesNotLoadLockedHoldout()
    {
        Assert.False(OfflineReviewCaseProvider.LoadsLockedHoldoutFlag);
        var offline = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "AdminReview", "OfflineReviewCaseProvider.cs"));
        Assert.DoesNotContain("location-matching-holdout.json", offline, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluate-locked-holdout", offline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LockedHoldoutEvaluation", offline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineProvider_DeduplicatesPerformanceIds()
    {
        var env = new TestHostEnvironment(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web"));
        var provider = new OfflineReviewCaseProvider(
            env,
            Options.Create(new AdaptiveLocationMatchingOptions()));
        var cases = await provider.GetCasesAsync(CancellationToken.None);
        Assert.NotEmpty(cases);
        Assert.Equal(cases.Count, cases.Select(item => item.PerformanceId).Distinct().Count());
        Assert.Equal(cases.Count, provider.UniqueCaseCount);
        Assert.True(provider.RawCaseCount >= provider.UniqueCaseCount);
        Assert.Equal(provider.RawCaseCount - provider.UniqueCaseCount, provider.DuplicatesRemoved);
        Assert.All(cases, item => Assert.Equal(AdminReviewStatus.Pending, item.ReviewStatus));
        Assert.All(
            cases.Where(item =>
                (item.MatcherStatus is "Unresolved" or "Ambiguous") &&
                item.Matcher.ProposedVisit is null),
            item =>
            {
                Assert.Null(item.Priority);
                Assert.Null(item.MaxDeviationMinutes);
            });
    }

    [Fact]
    public void NoExternalAiProvider_IsInitialized()
    {
        var di = File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "src",
                "TheBelgian.TimeControl.Infrastructure",
                "DependencyInjection.cs"));
        Assert.Contains("DeterministicReviewExplanationService", di, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", di, StringComparison.OrdinalIgnoreCase);
        Assert.False(LiveReviewCaseProvider.IsEnabled);
    }

    private static ReviewCase UnresolvedWithoutVisit(long id, string technician = "Tech")
    {
        var start = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse("2026-07-01T10:00:00+02:00", CultureInfo.InvariantCulture);
        return new ReviewCase(
            new SourceEvidence(id, new DateOnly(2026, 7, 1), technician, start, end, "x", null, null, null, null, null, null),
            new MatcherAssessment(
                "Unresolved",
                false,
                null,
                [],
                "test",
                GeocodeQualityClass.PartialAddress,
                null,
                null,
                null,
                "test",
                "hash"),
            new AdminDecision(AdminReviewStatus.Pending),
            null,
            ReviewWorkCategory.DataQuality,
            false,
            ["development"]);
    }

    private static ReviewCase Proposed(
        long id,
        int startDeviation,
        AdminReviewStatus status,
        string technician = "Tech",
        DateOnly? date = null)
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
                "Probable",
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

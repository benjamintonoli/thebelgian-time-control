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
    [InlineData(5, SpotcheckPriorityTier.PatternRelevant)]
    [InlineData(14, SpotcheckPriorityTier.PatternRelevant)]
    [InlineData(15, SpotcheckPriorityTier.IndividualException)]
    [InlineData(29, SpotcheckPriorityTier.IndividualException)]
    [InlineData(30, SpotcheckPriorityTier.HighPriority)]
    public void Priority_UsesDocumentedBands(int deviation, SpotcheckPriorityTier expected)
    {
        Assert.Equal(expected, SpotcheckPriorityCalculator.FromDeviationMinutes(deviation));
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
            OriginalMatcherStatus = "RecoveredProbable",
            ProposedVisitCandidateId = "a/b",
            ProposedVisitSourceStopIdsJson = "[\"a\",\"b\"]",
            Decision = nameof(AdminReviewStatus.Confirmed),
            ChosenVisitCandidateId = "c/d",
            ChosenVisitSourceStopIdsJson = "[\"c\",\"d\"]",
            ReasonOrComment = "andere kandidaat gekozen",
            Reviewer = "Ada",
            DecidedAt = DateTimeOffset.UtcNow,
            MatcherCommit = "abc",
            ConfigurationHash = "hash",
        };

        Assert.Equal("RecoveredProbable", audit.OriginalMatcherStatus);
        Assert.Equal("a/b", audit.ProposedVisitCandidateId);
        Assert.Equal("c/d", audit.ChosenVisitCandidateId);
        Assert.Equal(nameof(AdminReviewStatus.Confirmed), audit.Decision);
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
        var first = await repository.AppendAsync(
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
        var second = await repository.AppendAsync(
            new AdminReviewDecisionAudit
            {
                PerformanceId = 99,
                OriginalMatcherStatus = "Probable",
                ProposedVisitCandidateId = "a",
                Decision = nameof(AdminReviewStatus.Rejected),
                ChosenVisitCandidateId = null,
                ReasonOrComment = "toch niet",
                Reviewer = "Bea",
                DecidedAt = DateTimeOffset.Parse("2026-07-01T11:00:00Z", CultureInfo.InvariantCulture),
                MatcherCommit = "c1",
                ConfigurationHash = "h1",
            },
            CancellationToken.None);

        var trail = await repository.ListForPerformanceAsync(99, CancellationToken.None);
        Assert.Equal(2, trail.Count);
        Assert.Equal(first.Id, trail[0].Id);
        Assert.Equal(second.Id, trail[1].Id);
        Assert.Equal(nameof(AdminReviewStatus.Confirmed), trail[0].Decision);
        Assert.Equal(nameof(AdminReviewStatus.Rejected), trail[1].Decision);
        Assert.Equal("Probable", trail[0].OriginalMatcherStatus);
        Assert.Equal("Probable", trail[1].OriginalMatcherStatus);
    }

    [Fact]
    public void DeterministicExplanation_DistanceAndOverlap()
    {
        var service = new DeterministicReviewExplanationService();
        var source = new SourceEvidence(
            1,
            new DateOnly(2026, 7, 1),
            "Tech",
            DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-01T10:00:00+02:00", CultureInfo.InvariantCulture),
            "Straat 1",
            null,
            null,
            null,
            null,
            null,
            null);
        var visit = new ReviewVisitCandidate(
            "s1",
            ["s1"],
            null,
            DateTimeOffset.Parse("2026-07-01T09:05:00+02:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-01T09:55:00+02:00", CultureInfo.InvariantCulture),
            42,
            50,
            92,
            5,
            -5,
            "PartialAddress");
        var matcher = new MatcherAssessment(
            "Probable",
            true,
            visit,
            [visit],
            "voorstel",
            GeocodeQualityClass.PartialAddress,
            5,
            -5,
            5,
            "commit",
            "hash");

        var text = service.Explain(source, matcher);
        Assert.Contains("42 meter", text, StringComparison.Ordinal);
        Assert.Contains("92%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicExplanation_AmbiguousAndMissingAddress()
    {
        var service = new DeterministicReviewExplanationService();
        var source = new SourceEvidence(
            1,
            new DateOnly(2026, 7, 1),
            "Tech",
            DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-01T10:00:00+02:00", CultureInfo.InvariantCulture),
            " ",
            null,
            null,
            null,
            null,
            null,
            null);
        var matcher = new MatcherAssessment(
            "Ambiguous",
            false,
            null,
            [],
            "x",
            GeocodeQualityClass.Unusable,
            0,
            0,
            0,
            "c",
            "h");
        Assert.Contains(
            "niet betrouwbaar",
            service.Explain(source, matcher),
            StringComparison.OrdinalIgnoreCase);

        var withAddress = source with { PlenionAddress = "Straat 1" };
        var ambiguous = matcher with { GeocodeQuality = GeocodeQualityClass.PartialAddress };
        Assert.Contains(
            "vergelijkbaar sterk",
            service.Explain(withAddress, ambiguous),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdminReview_DoesNotLoadLockedHoldout()
    {
        Assert.False(OfflineReviewCaseProvider.LoadsLockedHoldoutFlag);
        Assert.False(new LiveReviewCaseProvider().LoadsLockedHoldout);
        var offline = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "AdminReview", "OfflineReviewCaseProvider.cs"));
        Assert.DoesNotContain("location-matching-holdout.json", offline, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluate-locked-holdout", offline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LockedHoldoutEvaluation", offline, StringComparison.Ordinal);
        Assert.Contains("DevelopmentFileName", offline, StringComparison.Ordinal);
        Assert.Contains("Calibration", offline, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlenionWritebackSurface_InAdminReview()
    {
        Assert.False(MatcherUsagePolicy.PlenionWritebackAllowed);
        var folder = Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Infrastructure",
            "AdminReview");
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("IPlenionWriter", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExecuteNonQuery", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ODBC", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE PRESTATIE", source, StringComparison.OrdinalIgnoreCase);
        }

        var service = File.ReadAllText(Path.Combine(folder, "AdminReviewService.cs"));
        Assert.Contains("Never writes to Plenion", service, StringComparison.Ordinal);
    }

    [Fact]
    public void NoExternalAiProvider_IsInitialized()
    {
        Assert.Equal(typeof(DeterministicReviewExplanationService), typeof(DeterministicReviewExplanationService));
        var di = File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "src",
                "TheBelgian.TimeControl.Infrastructure",
                "DependencyInjection.cs"));
        Assert.Contains("DeterministicReviewExplanationService", di, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", di, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AzureOpenAI", di, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChatClient", di, StringComparison.OrdinalIgnoreCase);

        var adminFolder = Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Infrastructure",
            "AdminReview");
        foreach (var file in Directory.GetFiles(adminFolder, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("OpenAI", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AzureOpenAI", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IChatClient", source, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(LiveReviewCaseProvider.IsEnabled);
    }

    [Fact]
    public void MatcherUsagePolicy_IsHumanReviewRequired_NoAutoAccept()
    {
        Assert.Equal(MatcherUsageMode.HumanReviewRequired, MatcherUsagePolicy.CurrentMode);
        Assert.False(MatcherUsagePolicy.AutomaticAcceptanceAllowed);
        Assert.Equal("NO-GO", MatcherUsagePolicy.HoldoutDecision);
    }

    [Fact]
    public async Task OfflineProvider_LoadsRealDocsWithoutHoldout()
    {
        var env = new TestHostEnvironment(Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Web"));
        var provider = new OfflineReviewCaseProvider(
            env,
            Options.Create(new AdaptiveLocationMatchingOptions()));
        Assert.Equal("OfflineReviewCaseProvider", provider.ProviderName);
        Assert.False(provider.LoadsLockedHoldout);
        var cases = await provider.GetCasesAsync(CancellationToken.None);
        Assert.NotEmpty(cases);
        Assert.All(cases, item => Assert.Equal(AdminReviewStatus.Pending, item.ReviewStatus));
    }

    private static ReviewCase Case(
        long id,
        int deviation,
        bool recurring,
        string matcherStatus,
        bool proposed = false)
    {
        var start = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse("2026-07-01T10:00:00+02:00", CultureInfo.InvariantCulture);
        var source = new SourceEvidence(
            id,
            new DateOnly(2026, 7, 1),
            "Tech",
            start,
            end,
            "x",
            null,
            null,
            null,
            null,
            null,
            null);
        var matcher = new MatcherAssessment(
            matcherStatus,
            proposed || matcherStatus is "Probable" or "Confirmed" or "RecoveredProbable",
            null,
            [],
            "test",
            GeocodeQualityClass.PartialAddress,
            deviation,
            0,
            deviation,
            "test",
            "hash");
        return new ReviewCase(
            source,
            matcher,
            new AdminDecision(AdminReviewStatus.Pending),
            SpotcheckPriorityCalculator.FromDeviationMinutes(deviation),
            recurring);
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

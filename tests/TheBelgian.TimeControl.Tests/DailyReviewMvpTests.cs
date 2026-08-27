using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests;

public sealed class DailyReviewMvpTests
{
    [Fact]
    public void NormalUi_RoundsGpsSeconds_ButEvidenceKeepsOriginalTimestamp()
    {
        var original = new DateTimeOffset(2026, 7, 13, 16, 2, 46, TimeSpan.FromHours(2));

        Assert.Equal("16:03", DailyReviewDisplay.ApproximateTime(original));
        Assert.Equal(46, original.Second);
    }

    [Fact]
    public void AuditResult_MapsToDailyReviewCase_WithSignedSecondsPreserved()
    {
        var cases = DailyReviewCaseMapper.Map(SampleJson, DateTimeOffset.UnixEpoch);

        var bart = Assert.Single(cases, item => item.Technician == "Bart Willocx");
        Assert.Equal(new DateOnly(2026, 7, 13), bart.Date);
        Assert.Equal(280204, bart.First.PerformanceId);
        Assert.Equal("Herlog", bart.First.Customer);
        Assert.Equal(-2.966666666666667, bart.First.SignedDifferenceMinutes!.Value, 8);
        Assert.Equal(57.233333333333334, bart.Last.SignedDifferenceMinutes!.Value, 8);
        Assert.Equal(DailyReviewEvidenceLevel.Complete, bart.EvidenceLevel);
        Assert.Equal(DailyReviewWorkflowStatus.Open, bart.Decision.Status);
    }

    [Fact]
    public void UnresolvedBoundary_IsNull_NotZero()
    {
        var cases = DailyReviewCaseMapper.Map(SampleJson, DateTimeOffset.UnixEpoch);

        var unresolved = Assert.Single(cases, item => item.Technician == "Unresolved Tech");
        Assert.Null(unresolved.First.GpsTime);
        Assert.Null(unresolved.First.SignedDifferenceMinutes);
        Assert.Null(unresolved.Last.SignedDifferenceMinutes);
        Assert.Equal(DailyReviewEvidenceLevel.Insufficient, unresolved.EvidenceLevel);
    }

    [Fact]
    public void WorksiteSessionEvidence_IsPreservedForCockpitExplanation()
    {
        var json = SampleJson.Replace(
            "\"EvidenceType\":0,\"IsReliable\":true,\"Reason\":\"technical end evidence\"",
            "\"EvidenceType\":4,\"IsReliable\":true,\"Reason\":\"WorksiteSession reconstructed\"",
            StringComparison.Ordinal);

        var bart = DailyReviewCaseMapper.Map(json, DateTimeOffset.UnixEpoch)
            .Single(item => item.Technician == "Bart Willocx");

        Assert.Equal("WorksiteSession", bart.Last.EvidenceType);
        Assert.Contains("WorksiteSession", bart.Last.TechnicalReason);
    }

    [Fact]
    public async Task ReviewStatusAndNote_AreSaved_AndClosedCaseKeepsOriginalEvidence()
    {
        await using var fixture = await ReviewFixture.CreateAsync();
        var reviewCase = Assert.Single(
            await fixture.Service.GetCockpitAsync(new DailyReviewFilter(), null, default)
                .ContinueWith(task => task.Result.Cases, TaskScheduler.Default),
            item => item.Technician == "Bart Willocx");

        await fixture.Service.SaveDecisionAsync(
            new SaveDailyReviewDecision(
                reviewCase.CaseId,
                DailyReviewWorkflowStatus.ResolvedNoAction,
                ReviewFeedbackReason.CorrectRegistration,
                "Ada Admin",
                "Ter plaatse bevestigd.",
                null,
                null),
            default);

        var reloaded = await fixture.Service.GetCaseAsync(reviewCase.CaseId, default);
        Assert.NotNull(reloaded);
        Assert.Equal(DailyReviewWorkflowStatus.ResolvedNoAction, reloaded.Decision.Status);
        Assert.Equal("Ter plaatse bevestigd.", reloaded.Decision.Notes);
        Assert.Equal(reviewCase.EvidenceSnapshotJson, reloaded.EvidenceSnapshotJson);
        var trail = await fixture.Repository.ListAsync(reviewCase.CaseId, default);
        var action = Assert.Single(trail);
        Assert.Equal(reviewCase.EvidenceSnapshotJson, action.EvidenceSnapshotJson);
    }

    [Fact]
    public async Task CorrectionProposal_PreservesOriginalTimes()
    {
        await using var fixture = await ReviewFixture.CreateAsync();
        var reviewCase = (await fixture.Service.GetCockpitAsync(
            new DailyReviewFilter(), null, default)).Cases
            .Single(item => item.Technician == "Bart Willocx");
        var proposedEnd = new DateTimeOffset(2026, 7, 13, 16, 3, 0, TimeSpan.FromHours(2));

        await fixture.Service.SaveDecisionAsync(
            new SaveDailyReviewDecision(
                reviewCase.CaseId,
                DailyReviewWorkflowStatus.PendingCorrection,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                "Voorstel, nog niet naar Plenion geschreven.",
                null,
                proposedEnd),
            default);

        await using var context = await fixture.Factory.CreateDbContextAsync();
        var proposal = Assert.Single(await context.DailyCorrectionProposals.ToListAsync());
        Assert.Equal(reviewCase.First.PlenionTime, proposal.OriginalStart);
        Assert.Equal(reviewCase.Last.PlenionTime, proposal.OriginalEnd);
        Assert.Equal(proposedEnd, proposal.ProposedEnd);
        Assert.Equal(nameof(DailyReviewWorkflowStatus.PendingCorrection), proposal.Status);
    }

    [Theory]
    [InlineData(DailyReviewWorkflowStatus.AwaitingExplanation)]
    [InlineData(DailyReviewWorkflowStatus.EscalatedForManagementReview)]
    public async Task ExplanationAndEscalation_RequireHumanNote(
        DailyReviewWorkflowStatus status)
    {
        await using var fixture = await ReviewFixture.CreateAsync();
        var reviewCase = (await fixture.Service.GetCockpitAsync(
            new DailyReviewFilter(), null, default)).Cases
            .Single(item => item.Technician == "Bart Willocx");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveDecisionAsync(
                new SaveDailyReviewDecision(
                    reviewCase.CaseId,
                    status,
                    ReviewFeedbackReason.Other,
                    "Ada Admin",
                    null,
                    null,
                    null),
                default));

        Assert.Equal("Deze actie vereist een korte notitie.", error.Message);
    }

    [Fact]
    public void FactualReport_UsesHumanLanguage_WithoutPrimaryMatcherTerms()
    {
        var reviewCase = DailyReviewCaseMapper.Map(SampleJson, DateTimeOffset.UnixEpoch)
            .Single(item => item.Technician == "Bart Willocx") with
        {
            Decision = new DailyReviewDecision(
                DailyReviewWorkflowStatus.EscalatedForManagementReview,
                ReviewFeedbackReason.UnexplainedMismatch,
                "Manueel geselecteerd voor verdere beoordeling.",
                "Ada Admin",
                DateTimeOffset.UnixEpoch,
                null,
                null),
        };

        var report = DailyFactualReportBuilder.Build(
            [reviewCase],
            "Ada Admin",
            DateTimeOffset.UnixEpoch);

        Assert.Contains("Geregistreerde start eerste klantprestatie: 08:30", report);
        Assert.Contains("GPS-aankomst op werklocatie: 08:27:02", report);
        Assert.Contains("GPS-vertrek van werklocatie: 16:02:46", report);
        Assert.Contains("ongeveer 57 minuten", report);
        Assert.Contains("Adminnotitie", report);
        Assert.DoesNotContain("FIRST boundary", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LAST boundary", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MatcherStatus", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ContextSupported", report, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ReviewFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _jsonPath;

        private ReviewFixture(
            SqliteConnection connection,
            string jsonPath,
            TestDbContextFactory factory,
            DailyReviewRepository repository,
            DailyReviewService service)
        {
            _connection = connection;
            _jsonPath = jsonPath;
            Factory = factory;
            Repository = repository;
            Service = service;
        }

        public TestDbContextFactory Factory { get; }
        public DailyReviewRepository Repository { get; }
        public DailyReviewService Service { get; }

        public static async Task<ReviewFixture> CreateAsync()
        {
            var jsonPath = Path.Combine(Path.GetTempPath(), $"daily-review-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(jsonPath, SampleJson);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DailyReviewData:AuditJsonPath"] = jsonPath,
                })
                .Build();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TimeControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestDbContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var repository = new DailyReviewRepository(factory);
            var provider = new DailyAuditReviewCaseProvider(configuration);
            var service = new DailyReviewService(provider, repository, TimeProvider.System);
            return new ReviewFixture(connection, jsonPath, factory, repository, service);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            File.Delete(_jsonPath);
        }
    }

    internal sealed class TestDbContextFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);

        public Task<TimeControlDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private const string SampleJson = """
        [
          {
            "Date":"2026-07-13","Technician":"Bart Willocx",
            "TotalConfirmedDeviation":57,"TotalReviewPotentialDeviation":0,"ReviewStatus":"Reliable",
            "Performances":[{"PerformanceId":280204,"Customer":"Herlog","Address":"Atealaan 34a / 2200 / Herentals"}],
            "First":{"PerformanceId":280204,"PlenionAddress":"Atealaan 34a / 2200 / Herentals","MatcherStatus":"Probable","Score":66.1,"DistanceMeters":242,"OverlapMinutes":276,"SelectedVisitId":"visit-a"},
            "Last":{"PerformanceId":280204,"PlenionAddress":"Atealaan 34a / 2200 / Herentals","MatcherStatus":"Confirmed","Score":91.1,"DistanceMeters":63.7,"OverlapMinutes":75,"SelectedVisitId":"visit-b"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-13T08:30:00+02:00","ExactSiteBoundaryTime":"2026-07-13T08:27:02+02:00","EffectiveBoundaryTime":"2026-07-13T08:27:02+02:00","EvidenceType":0,"IsReliable":true,"Reason":"technical start evidence"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-13T17:00:00+02:00","ExactSiteBoundaryTime":"2026-07-13T16:02:46+02:00","EffectiveBoundaryTime":"2026-07-13T16:02:46+02:00","EvidenceType":0,"IsReliable":true,"Reason":"technical end evidence"}
          },
          {
            "Date":"2026-07-14","Technician":"Unresolved Tech",
            "TotalConfirmedDeviation":0,"TotalReviewPotentialDeviation":5,"ReviewStatus":"Unresolved",
            "Performances":[{"PerformanceId":2,"Customer":"Testsite","Address":"Teststraat 1"}],
            "First":{"PerformanceId":2,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved","Score":null,"DistanceMeters":null,"OverlapMinutes":null,"SelectedVisitId":null},
            "Last":{"PerformanceId":2,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved","Score":null,"DistanceMeters":null,"OverlapMinutes":null,"SelectedVisitId":null},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-14T08:00:00+02:00","ExactSiteBoundaryTime":null,"EffectiveBoundaryTime":null,"EvidenceType":3,"IsReliable":false,"Reason":"none"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-14T16:00:00+02:00","ExactSiteBoundaryTime":null,"EffectiveBoundaryTime":null,"EvidenceType":3,"IsReliable":false,"Reason":"none"}
          }
        ]
        """;
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Tests;

public sealed class MonthlyReviewWorkflowTests
{
    [Theory]
    [InlineData(2026, 9, 10, 2026, 7)]
    [InlineData(2026, 9, 15, 2026, 8)]
    [InlineData(2026, 8, 26, 2026, 7)]
    [InlineData(2027, 1, 10, 2026, 11)]
    [InlineData(2027, 1, 15, 2026, 12)]
    public async Task DefaultMonth_UsesFifteenthAndHandlesYearBoundaries(
        int year, int month, int day, int expectedYear, int expectedMonth)
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = fixture.Service.GetDefaultMonth(
            new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(new ReviewMonth(expectedYear, expectedMonth), result);
    }

    [Fact]
    public async Task Prepare_IsIdempotent_AndSeparatesOrdinaryDataQualityAndNoTrack()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.PrepareAsync(SampleJson("evidence-v1"));
        var second = await fixture.PrepareAsync(SampleJson("evidence-v1"));
        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), null, default);
        var ordinaryQueue = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.Open), null, default);

        Assert.Equal(3, first.NewCases);
        Assert.Equal(0, second.NewCases);
        Assert.Equal(3, second.UnchangedCases);
        Assert.Equal(3, await fixture.CountPeriodsAsync());
        Assert.Equal(1, cockpit.Review.Counts.Open);
        Assert.Equal(1, cockpit.Review.Counts.DataQuality);
        Assert.Equal(1, cockpit.Review.Counts.NotApplicable);
        Assert.DoesNotContain(ordinaryQueue.Review.Cases, item =>
            item.AuditReviewStatus.Contains("NoTrackAndTrace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refresh_PreservesReviewForIdenticalEvidence_ButChangedEvidenceNeedsReReview()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(SampleJson("evidence-v1"));
        var initial = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.Open), null, default);
        var reviewCase = Assert.Single(initial.Review.Cases);
        await fixture.Service.SaveDecisionAsync(July, new SaveDailyReviewDecision(
            reviewCase.CaseId,
            DailyReviewWorkflowStatus.ResolvedNoAction,
            ReviewFeedbackReason.CorrectRegistration,
            "Ada Admin", "Gecontroleerd", null, null), default);

        await fixture.PrepareAsync(SampleJson("evidence-v1"));
        var unchanged = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        Assert.Equal(DailyReviewWorkflowStatus.ResolvedNoAction,
            unchanged.Review.Selected!.Decision.Status);
        Assert.Equal(MonthlyReviewStatus.InReview, unchanged.Period.Status);

        var changedResult = await fixture.PrepareAsync(SampleJson("evidence-v2"));
        var changed = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        Assert.Equal(1, changedResult.ChangedCases);
        Assert.Equal(DailyReviewWorkflowStatus.NeedsReReview,
            changed.Review.Selected!.Decision.Status);
        Assert.Contains("opnieuw controleren", changed.Review.Selected.Decision.Notes);
        Assert.Single(await fixture.ActionsAsync(reviewCase.CaseId));
    }

    [Fact]
    public async Task Finalize_BlocksOpenCases_ThenFreezesSnapshotAndMakesReportDefinitive()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(SampleJson("evidence-v1"));
        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.Open), null, default);
        var reviewCase = Assert.Single(cockpit.Review.Cases);
        var draft = await fixture.Service.BuildHtmlReportAsync(July, default);
        Assert.Contains("VOORLOPIG", draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.FinalizeAsync(July, "Ada Admin", false, default));
        await fixture.Service.SaveDecisionAsync(July, new SaveDailyReviewDecision(
            reviewCase.CaseId, DailyReviewWorkflowStatus.ResolvedNoAction,
            ReviewFeedbackReason.CorrectRegistration, "Ada Admin", null, null, null), default);

        var finalized = await fixture.Service.FinalizeAsync(July, "Ada Admin", false, default);
        var attemptedRefresh = await fixture.PrepareAsync(SampleJson("changed-after-finalize"));
        var report = await fixture.Service.BuildHtmlReportAsync(July, default);

        Assert.Equal(MonthlyReviewStatus.Finalized, finalized.Status);
        Assert.NotNull(finalized.FinalSnapshotJson);
        Assert.Equal("FinalizedSnapshot", attemptedRefresh.EvidenceSource);
        Assert.DoesNotContain("VOORLOPIG", report);
        Assert.Contains("Status: Definitief", report);
    }

    private static readonly ReviewMonth July = new(2026, 7);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _directory;

        private Fixture(
            SqliteConnection connection,
            string directory,
            TestFactory factory,
            MonthlyReviewService service)
        {
            _connection = connection;
            _directory = directory;
            Factory = factory;
            Service = service;
        }

        public TestFactory Factory { get; }
        public MonthlyReviewService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TimeControlDbContext>()
                .UseSqlite(connection).Options;
            var factory = new TestFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.FromHours(2));
            var time = new FixedTimeProvider(now);
            var repository = new DailyReviewRepository(factory);
            var history = new VehicleAssignmentSyncHistoryService(factory, time);
            var directory = Path.Combine(Path.GetTempPath(), $"monthly-review-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var service = new MonthlyReviewService(
                factory, null!, repository, history, null!,
                Options.Create(new TimeControlCorrectionWriteOptions()), time);
            return new Fixture(connection, directory, factory, service);
        }

        public async Task<MonthlyPrepareResult> PrepareAsync(string json)
        {
            var path = Path.Combine(_directory, "evidence.json");
            await File.WriteAllTextAsync(path, json);
            return await Service.PrepareAsync(July, "SYSTEM", path, true, default);
        }

        public async Task<int> CountPeriodsAsync()
        {
            await using var context = await Factory.CreateDbContextAsync();
            var periods = await context.MonthlyReviewPeriods.CountAsync();
            var cases = await context.MonthlyReviewCaseSnapshots.CountAsync();
            return periods + cases - 1;
        }

        public async Task<DailyReviewActionAudit[]> ActionsAsync(string caseId)
        {
            await using var context = await Factory.CreateDbContextAsync();
            return await context.DailyReviewActionAudits.Where(item => item.CaseId == caseId)
                .ToArrayAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class TestFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);
        public Task<TimeControlDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string SampleJson(string evidenceReason) => $$"""
        [
          {
            "Date":"2026-07-13","Technician":"Bart Willocx",
            "TotalConfirmedDeviation":57,"TotalReviewPotentialDeviation":0,"ReviewStatus":"Reliable",
            "Performances":[{"PerformanceId":280204,"Customer":"Herlog"}],
            "First":{"PerformanceId":280204,"PlenionAddress":"Atealaan 34a","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"a"},
            "Last":{"PerformanceId":280204,"PlenionAddress":"Atealaan 34a","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"b"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-13T08:30:00+02:00","EffectiveBoundaryTime":"2026-07-13T08:31:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"{{evidenceReason}}"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-13T17:00:00+02:00","EffectiveBoundaryTime":"2026-07-13T16:03:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"end"}
          },
          {
            "Date":"2026-07-14","Technician":"Insufficient Tech",
            "TotalConfirmedDeviation":0,"TotalReviewPotentialDeviation":20,"ReviewStatus":"InsufficientVehicleAssignment",
            "Performances":[{"PerformanceId":2,"Customer":"Testsite"}],
            "First":{"PerformanceId":2,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved"},
            "Last":{"PerformanceId":2,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-14T08:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"InsufficientVehicleAssignment"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-14T16:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"InsufficientVehicleAssignment"}
          },
          {
            "Date":"2026-07-15","Technician":"No Track Tech",
            "TotalConfirmedDeviation":0,"TotalReviewPotentialDeviation":20,"ReviewStatus":"ExcludedNoTrackAndTrace",
            "Performances":[{"PerformanceId":3,"Customer":"Testsite"}],
            "First":{"PerformanceId":3,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved"},
            "Last":{"PerformanceId":3,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-15T08:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"NoTrackAndTrace"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-15T16:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"NoTrackAndTrace"}
          }
        ]
        """;
}

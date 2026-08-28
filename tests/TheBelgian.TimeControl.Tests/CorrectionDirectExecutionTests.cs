using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Tests;

public sealed class CorrectionDirectExecutionTests
{
    private static readonly ReviewMonth July = new(2026, 7);

    [Fact]
    public async Task DirectExecute_CreatesApprovedProposalInternallyAndMarksExecuted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(CorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();
        var proposedStart = reviewCase.First.PlenionTime.AddMinutes(1);

        var result = await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                proposedStart,
                null),
            default);

        Assert.Equal(CorrectionProposalStatuses.Executed, result.Status);
        Assert.Equal(
            proposedStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            result.Proposal.ProposedStart!.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(result.Proposal.ProposedEnd);
        Assert.Equal("Ada Admin", result.Proposal.ExecutedBy);
        Assert.NotNull(result.Proposal.ExecutedAt);

        var proposals = await fixture.ProposalsAsync(reviewCase.CaseId);
        Assert.Single(proposals);
        Assert.Equal(CorrectionProposalStatuses.Executed, proposals[0].Status);

        var actions = await fixture.ActionsAsync(reviewCase.CaseId);
        Assert.Contains(actions, item => item.Decision == DailyReviewWorkflowStatus.PendingCorrection.ToString());
        Assert.Contains(actions, item => item.Decision == DailyReviewWorkflowStatus.CorrectionExecuted.ToString());
        Assert.Equal(1, fixture.Client.ExecuteCalls);
    }

    [Fact]
    public async Task DirectExecute_WithoutPriorAdministrativeDecision_IsAllowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(CorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        Assert.Empty(await fixture.ActionsAsync(reviewCase.CaseId));

        var result = await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                reviewCase.First.PlenionTime.AddMinutes(2),
                null),
            default);

        Assert.Equal(CorrectionProposalStatuses.Executed, result.Status);
    }

    [Fact]
    public async Task DirectExecute_SameAsRegisteredTime_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(CorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteDirectCorrectionAsync(
                July,
                new ExecuteDirectCorrectionRequest(
                    reviewCase.CaseId,
                    ReviewFeedbackReason.AdministrativeEntryError,
                    "Ada Admin",
                    null,
                    reviewCase.First.PlenionTime,
                    null),
                default));

        Assert.Contains("nieuwe start- en/of eindtijd", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Client.ExecuteCalls);
    }

    [Fact]
    public async Task DirectExecute_PartialOnlyStartReliable_RejectsEndCorrection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(PartialStartOnlyEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteDirectCorrectionAsync(
                July,
                new ExecuteDirectCorrectionRequest(
                    reviewCase.CaseId,
                    ReviewFeedbackReason.AdministrativeEntryError,
                    "Ada Admin",
                    null,
                    null,
                    reviewCase.Last.PlenionTime.AddMinutes(-5)),
                default));

        Assert.Contains("eindeboundary", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Client.ExecuteCalls);
    }

    [Fact]
    public async Task DirectExecute_UnresolvedCase_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(UnresolvedEvidenceJson());
        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.DataQuality), null, default);
        var reviewCase = Assert.Single(cockpit.Review.Cases);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteDirectCorrectionAsync(
                July,
                new ExecuteDirectCorrectionRequest(
                    reviewCase.CaseId,
                    ReviewFeedbackReason.AdministrativeEntryError,
                    "Ada Admin",
                    null,
                    reviewCase.First.PlenionTime.AddMinutes(5),
                    null),
                default));

        Assert.Contains("betrouwbare", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Client.ExecuteCalls);
    }

    [Fact]
    public async Task DirectExecute_Conflict_MarksNeedsReReviewWithoutWriteSuccess()
    {
        await using var fixture = await Fixture.CreateAsync(conflict: true);
        await fixture.PrepareAsync(CorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        var result = await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                reviewCase.First.PlenionTime.AddMinutes(1),
                null),
            default);

        Assert.Equal(CorrectionProposalStatuses.Conflict, result.Status);
        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        Assert.Equal(DailyReviewWorkflowStatus.NeedsReReview, cockpit.Review.Selected!.Decision.Status);
        Assert.Equal(1, fixture.Client.ExecuteCalls);
    }

    [Fact]
    public async Task DirectExecute_IdempotentForAlreadyExecutedSameTargets()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(CorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();
        var proposedStart = reviewCase.First.PlenionTime.AddMinutes(1);
        var request = new ExecuteDirectCorrectionRequest(
            reviewCase.CaseId,
            ReviewFeedbackReason.AdministrativeEntryError,
            "Ada Admin",
            null,
            proposedStart,
            null);

        var first = await fixture.Service.ExecuteDirectCorrectionAsync(July, request, default);
        var second = await fixture.Service.ExecuteDirectCorrectionAsync(July, request, default);

        Assert.Equal(CorrectionProposalStatuses.Executed, first.Status);
        Assert.Equal(CorrectionProposalStatuses.Executed, second.Status);
        Assert.Equal(1, fixture.Client.ExecuteCalls);
        Assert.Single(await fixture.ProposalsAsync(reviewCase.CaseId));
    }

    [Fact]
    public async Task WritesDisabled_BlocksDirectExecute()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        await fixture.PrepareAsync(CorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteDirectCorrectionAsync(
                July,
                new ExecuteDirectCorrectionRequest(
                    reviewCase.CaseId,
                    ReviewFeedbackReason.AdministrativeEntryError,
                    "Ada Admin",
                    null,
                    reviewCase.First.PlenionTime.AddMinutes(1),
                    null),
                default));

        Assert.Contains("uitgeschakeld", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Client.ExecuteCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _directory;

        private Fixture(
            SqliteConnection connection,
            string directory,
            TestFactory factory,
            MonthlyReviewService service,
            RecordingCorrectionClient client)
        {
            _connection = connection;
            _directory = directory;
            Factory = factory;
            Service = service;
            Client = client;
        }

        public TestFactory Factory { get; }
        public MonthlyReviewService Service { get; }
        public RecordingCorrectionClient Client { get; }

        public static async Task<Fixture> CreateAsync(bool enabled = true, bool conflict = false)
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
            var directory = Path.Combine(Path.GetTempPath(), $"direct-correction-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var client = new RecordingCorrectionClient(conflict);
            var service = new MonthlyReviewService(
                factory, null!, repository, history, client,
                Options.Create(new TimeControlCorrectionWriteOptions
                {
                    Enabled = enabled,
                    UseMock = true,
                    BaseUrl = "http://localhost:5090",
                }),
                time);
            return new Fixture(connection, directory, factory, service, client);
        }

        public async Task<MonthlyPrepareResult> PrepareAsync(string json)
        {
            var path = Path.Combine(_directory, "evidence.json");
            await File.WriteAllTextAsync(path, json);
            return await Service.PrepareAsync(July, "SYSTEM", path, true, default);
        }

        public async Task<DailyReviewCase> OrdinaryCaseAsync()
        {
            var cockpit = await Service.GetCockpitAsync(
                July, new DailyReviewFilter(DailyReviewQueueView.Open), null, default);
            return Assert.Single(cockpit.Review.Cases);
        }

        public async Task<DailyCorrectionProposal[]> ProposalsAsync(string caseId)
        {
            await using var context = await Factory.CreateDbContextAsync();
            return await context.DailyCorrectionProposals.Where(item => item.CaseId == caseId)
                .OrderBy(item => item.Id).ToArrayAsync();
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

    internal sealed class RecordingCorrectionClient(bool conflict) : IPlenionCorrectionClient
    {
        public int ExecuteCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<PlenionCorrectionResponse> ExecuteAsync(
            PlenionCorrectionCommand command,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            if (conflict)
            {
                return Task.FromResult(new PlenionCorrectionResponse(
                    "conflict",
                    "Record changed",
                    "conflict-ref",
                    command.IdempotencyKey,
                    []));
            }

            return Task.FromResult(new PlenionCorrectionResponse(
                "success",
                "ok",
                "ref-" + command.IdempotencyKey,
                command.IdempotencyKey,
                command.Corrections.Select(item => new PlenionCorrectionResultItem(
                    item.PerformanceId,
                    item.OriginalStart,
                    item.OriginalEnd,
                    item.NewStart ?? item.OriginalStart,
                    item.NewEnd ?? item.OriginalEnd)).ToArray()));
        }
    }

    private static string CorrectionEvidenceJson() => """
        [
          {
            "Date":"2026-07-13","Technician":"Bart Willocx",
            "TotalConfirmedDeviation":57,"TotalReviewPotentialDeviation":0,"ReviewStatus":"Reliable",
            "Performances":[{
              "PerformanceId":280204,"Customer":"Herlog","ActivityType":"CustomerWork",
              "Start":"2026-07-13T08:30:00+02:00","End":"2026-07-13T17:00:00+02:00",
              "MainTaskExternalId":30
            }],
            "First":{"PerformanceId":280204,"PlenionAddress":"Atealaan 34a","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"a"},
            "Last":{"PerformanceId":280204,"PlenionAddress":"Atealaan 34a","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"b"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-13T08:30:00+02:00","EffectiveBoundaryTime":"2026-07-13T08:31:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"exact"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-13T17:00:00+02:00","EffectiveBoundaryTime":"2026-07-13T16:03:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"end"}
          }
        ]
        """;

    private static string PartialStartOnlyEvidenceJson() => """
        [
          {
            "Date":"2026-07-13","Technician":"Partial Tech",
            "TotalConfirmedDeviation":20,"TotalReviewPotentialDeviation":0,"ReviewStatus":"Reliable",
            "Performances":[{
              "PerformanceId":280205,"Customer":"Site","ActivityType":"CustomerWork",
              "Start":"2026-07-13T08:00:00+02:00","End":"2026-07-13T16:00:00+02:00",
              "MainTaskExternalId":30
            }],
            "First":{"PerformanceId":280205,"PlenionAddress":"Site 1","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"a"},
            "Last":{"PerformanceId":280205,"PlenionAddress":"Site 1","MatcherStatus":"Unresolved","Score":10,"DistanceMeters":500,"OverlapMinutes":0,"SelectedVisitId":null},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-13T08:00:00+02:00","EffectiveBoundaryTime":"2026-07-13T08:15:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"exact"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-13T16:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"Unresolved"}
          }
        ]
        """;

    private static string UnresolvedEvidenceJson() => """
        [
          {
            "Date":"2026-07-14","Technician":"Unresolved Tech",
            "TotalConfirmedDeviation":0,"TotalReviewPotentialDeviation":20,"ReviewStatus":"Unresolved",
            "Performances":[{
              "PerformanceId":2,"Customer":"Testsite","ActivityType":"CustomerWork",
              "Start":"2026-07-14T08:00:00+02:00","End":"2026-07-14T16:00:00+02:00",
              "MainTaskExternalId":30
            }],
            "First":{"PerformanceId":2,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved"},
            "Last":{"PerformanceId":2,"PlenionAddress":"Teststraat 1","MatcherStatus":"Unresolved"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-14T08:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"Unresolved"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-14T16:00:00+02:00","EvidenceType":3,"IsReliable":false,"Reason":"Unresolved"}
          }
        ]
        """;
}

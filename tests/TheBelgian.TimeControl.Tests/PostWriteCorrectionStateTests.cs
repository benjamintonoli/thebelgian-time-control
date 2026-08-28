using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Tests;

public sealed class PostWriteCorrectionStateTests
{
    private static readonly ReviewMonth July = new(2026, 7);

    [Fact]
    public async Task SuccessfulStartCorrection_PreservesOriginalInProposal_AndShowsCorrectedBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(StartCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();
        var originalStart = reviewCase.First.PlenionTime;
        var proposedStart = originalStart.AddMinutes(1);

        await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                proposedStart,
                null),
            default);

        var proposal = Assert.Single(await fixture.ProposalsAsync(reviewCase.CaseId));
        Assert.Equal(originalStart, proposal.OriginalStart);
        Assert.Equal(proposedStart, proposal.ProposedStart);
        Assert.Equal(CorrectionProposalStatuses.Executed, proposal.Status);

        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        var selected = cockpit.Review.Selected!;
        Assert.Equal(proposedStart, selected.First.PlenionTime);
        Assert.Equal(reviewCase.Last.PlenionTime, selected.Last.PlenionTime);
    }

    [Fact]
    public async Task SuccessfulEndCorrection_PreservesOriginalInProposal_AndShowsCorrectedBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(EndCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();
        var originalEnd = reviewCase.Last.PlenionTime;
        var proposedEnd = originalEnd.AddMinutes(-8);

        await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                null,
                proposedEnd),
            default);

        var proposal = Assert.Single(await fixture.ProposalsAsync(reviewCase.CaseId));
        Assert.Equal(originalEnd, proposal.OriginalEnd);
        Assert.Equal(proposedEnd, proposal.ProposedEnd);
        Assert.Equal(CorrectionProposalStatuses.Executed, proposal.Status);

        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        var selected = cockpit.Review.Selected!;
        Assert.Equal(reviewCase.First.PlenionTime, selected.First.PlenionTime);
        Assert.Equal(proposedEnd, selected.Last.PlenionTime);
    }

    [Fact]
    public async Task SuccessfulEndCorrection_StoresReviewerSubjectOnProposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(EndCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        var result = await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "benjamin.tonoli@thebelgian.be",
                null,
                null,
                reviewCase.Last.PlenionTime.AddMinutes(-8),
                "entra-subject-123"),
            default);

        Assert.Equal(CorrectionProposalStatuses.Executed, result.Status);
        Assert.Equal("benjamin.tonoli@thebelgian.be", result.Proposal.ExecutedBy);
        Assert.Equal("entra-subject-123", result.Proposal.ExecutedBySubject);
    }

    [Fact]
    public async Task ExecutedCorrection_AppearsInCompletedQueue_NotOpenQueue()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(EndCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                null,
                reviewCase.Last.PlenionTime.AddMinutes(-8)),
            default);

        var open = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.Open), reviewCase.CaseId, default);
        Assert.DoesNotContain(open.Review.Cases, item => item.CaseId == reviewCase.CaseId);

        var completed = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.Completed), reviewCase.CaseId, default);
        var completedCase = Assert.Single(completed.Review.Cases);
        Assert.Equal(DailyReviewWorkflowStatus.CorrectionExecuted, completedCase.Decision.Status);
    }

    [Fact]
    public async Task SuccessfulOwnWrite_DoesNotShowNeedsReReviewWarning()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(EndCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                null,
                reviewCase.Last.PlenionTime.AddMinutes(-8)),
            default);

        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        Assert.Equal(DailyReviewWorkflowStatus.CorrectionExecuted, cockpit.Review.Selected!.Decision.Status);
    }

    [Fact]
    public async Task ExternalEvidenceChange_StillShowsNeedsReReview()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(EndCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();

        await using (var context = await fixture.Factory.CreateDbContextAsync())
        {
            var snapshot = await context.MonthlyReviewCaseSnapshots.SingleAsync(
                item => item.CaseId == reviewCase.CaseId);
            snapshot.EvidenceSnapshotJson = snapshot.EvidenceSnapshotJson.Replace(
                "16:03:00", "16:10:00", StringComparison.Ordinal);
            snapshot.NeedsReReview = true;
            await context.SaveChangesAsync();
        }

        var cockpit = await fixture.Service.GetCockpitAsync(
            July, new DailyReviewFilter(DailyReviewQueueView.All), reviewCase.CaseId, default);
        Assert.Equal(DailyReviewWorkflowStatus.NeedsReReview, cockpit.Review.Selected!.Decision.Status);
    }

    [Fact]
    public async Task ConflictHistory_RemainsVisibleAfterSuccessfulRetry()
    {
        await using var fixture = await Fixture.CreateAsync(conflictThenSuccess: true);
        await fixture.PrepareAsync(StartCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();
        var proposedStart = reviewCase.First.PlenionTime.AddMinutes(1);

        var conflict = await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                proposedStart,
                null),
            default);
        Assert.Equal(CorrectionProposalStatuses.Conflict, conflict.Status);

        fixture.Client.Conflict = false;
        var success = await fixture.Service.ExecuteDirectCorrectionAsync(
            July,
            new ExecuteDirectCorrectionRequest(
                reviewCase.CaseId,
                ReviewFeedbackReason.AdministrativeEntryError,
                "Ada Admin",
                null,
                proposedStart,
                null),
            default);
        Assert.Equal(CorrectionProposalStatuses.Executed, success.Status);

        var proposals = await fixture.ProposalsAsync(reviewCase.CaseId);
        Assert.Equal(2, proposals.Length);
        Assert.Equal(CorrectionProposalStatuses.Conflict, proposals[0].Status);
        Assert.Equal(CorrectionProposalStatuses.Executed, proposals[1].Status);

        var audits = await fixture.ActionsAsync(reviewCase.CaseId);
        Assert.Contains(audits, item => item.Decision == DailyReviewWorkflowStatus.NeedsReReview.ToString());
        Assert.Contains(audits, item => item.Decision == DailyReviewWorkflowStatus.CorrectionExecuted.ToString());
    }

    [Fact]
    public async Task Idempotency_PreventsDuplicateExecuteForSameTargets()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PrepareAsync(StartCorrectionEvidenceJson());
        var reviewCase = await fixture.OrdinaryCaseAsync();
        var request = new ExecuteDirectCorrectionRequest(
            reviewCase.CaseId,
            ReviewFeedbackReason.AdministrativeEntryError,
            "Ada Admin",
            null,
            reviewCase.First.PlenionTime.AddMinutes(1),
            null);

        await fixture.Service.ExecuteDirectCorrectionAsync(July, request, default);
        await fixture.Service.ExecuteDirectCorrectionAsync(July, request, default);

        Assert.Equal(1, fixture.Client.ExecuteCalls);
        Assert.Single(await fixture.ProposalsAsync(reviewCase.CaseId));
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
            ToggleCorrectionClient client)
        {
            _connection = connection;
            _directory = directory;
            Factory = factory;
            Service = service;
            Client = client;
        }

        public TestFactory Factory { get; }
        public MonthlyReviewService Service { get; }
        public ToggleCorrectionClient Client { get; }

        public static async Task<Fixture> CreateAsync(bool conflictThenSuccess = false)
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
            var directory = Path.Combine(Path.GetTempPath(), $"post-write-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var client = new ToggleCorrectionClient(conflictThenSuccess);
            var service = new MonthlyReviewService(
                factory, null!, repository, history, client,
                Options.Create(new TimeControlCorrectionWriteOptions
                {
                    Enabled = true,
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
                .OrderBy(item => item.Id).ToArrayAsync();
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

    private sealed class ToggleCorrectionClient(bool conflictInitially) : IPlenionCorrectionClient
    {
        public bool Conflict { get; set; } = conflictInitially;
        public int ExecuteCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<PlenionCorrectionResponse> ExecuteAsync(
            PlenionCorrectionCommand command,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            if (Conflict)
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

    private static string StartCorrectionEvidenceJson() => """
        [
          {
            "Date":"2026-07-13","Technician":"Start Tech",
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

    private static string EndCorrectionEvidenceJson() => """
        [
          {
            "Date":"2026-07-28","Technician":"End Tech",
            "TotalConfirmedDeviation":57,"TotalReviewPotentialDeviation":0,"ReviewStatus":"Reliable",
            "Performances":[{
              "PerformanceId":280663,"Customer":"Site","ActivityType":"CustomerWork",
              "Start":"2026-07-28T08:00:00+02:00","End":"2026-07-28T16:30:00+02:00",
              "MainTaskExternalId":9
            }],
            "First":{"PerformanceId":280663,"PlenionAddress":"Site 1","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"a"},
            "Last":{"PerformanceId":280663,"PlenionAddress":"Site 1","MatcherStatus":"Confirmed","Score":90,"DistanceMeters":20,"OverlapMinutes":30,"SelectedVisitId":"b"},
            "FirstEvidence":{"PlenionBoundaryTime":"2026-07-28T08:00:00+02:00","EffectiveBoundaryTime":"2026-07-28T08:01:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"exact"},
            "LastEvidence":{"PlenionBoundaryTime":"2026-07-28T16:30:00+02:00","EffectiveBoundaryTime":"2026-07-28T16:22:00+02:00","EvidenceType":0,"IsReliable":true,"Reason":"end"}
          }
        ]
        """;
}

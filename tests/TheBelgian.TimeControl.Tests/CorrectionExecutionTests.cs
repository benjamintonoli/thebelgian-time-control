using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Tests;

public sealed class CorrectionExecutionTests
{
    [Fact]
    public async Task MockClient_IsAvailable_AndReturnsVerifiedTargets()
    {
        var client = new MockPlenionCorrectionClient();
        Assert.True(await client.IsAvailableAsync(default));
        var response = await client.ExecuteAsync(new PlenionCorrectionCommand(
            [Item(10, TimeSpan.FromHours(7), null)], "reason", "reviewer", "case", "key"), default);
        Assert.Equal("success", response.Status);
        Assert.Equal(TimeSpan.FromHours(7), response.Performances.Single().CurrentStart);
    }

    [Fact]
    public async Task DisabledHttpClient_DoesNotContactPws()
    {
        var handler = new CountingHandler();
        var client = new HttpPlenionCorrectionClient(new HttpClient(handler),
            Options.Create(new TimeControlCorrectionWriteOptions { Enabled = false }));
        Assert.False(await client.IsAvailableAsync(default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void ReliableStart_UsesFirstPerformanceRecord()
    {
        var command = MonthlyReviewService.BuildCorrectionCommand(
            Proposal(firstId: 101, lastId: 202, proposedStart: At(7, 6), proposedEnd: null),
            new ReviewMonth(2026, 7), "Benjamin");
        var item = Assert.Single(command.Corrections);
        Assert.Equal(101, item.PerformanceId);
        Assert.Equal(TimeSpan.FromHours(8), item.OriginalEnd);
    }

    [Fact]
    public void ReliableEnd_UsesLastPerformanceRecord()
    {
        var command = MonthlyReviewService.BuildCorrectionCommand(
            Proposal(firstId: 101, lastId: 202, proposedStart: null, proposedEnd: At(12, 23)),
            new ReviewMonth(2026, 7), "Benjamin");
        var item = Assert.Single(command.Corrections);
        Assert.Equal(202, item.PerformanceId);
        Assert.Equal(TimeSpan.FromHours(10), item.OriginalStart);
    }

    [Fact]
    public void DifferentFirstAndLastRecords_CreateAtomicTwoItemCommand()
    {
        var command = MonthlyReviewService.BuildCorrectionCommand(
            Proposal(101, 202, At(7, 6), At(12, 23)), new ReviewMonth(2026, 7), "Benjamin");
        Assert.Equal([101L, 202L], command.Corrections.Select(item => item.PerformanceId));
    }

    [Fact]
    public void SameFirstAndLastRecord_CreatesOneCombinedItem()
    {
        var proposal = Proposal(101, 101, At(7, 6), At(12, 23));
        proposal.LastRecordOriginalStart = proposal.FirstRecordOriginalStart;
        proposal.LastRecordOriginalEnd = proposal.FirstRecordOriginalEnd;
        var item = Assert.Single(MonthlyReviewService.BuildCorrectionCommand(
            proposal, new ReviewMonth(2026, 7), "Benjamin").Corrections);
        Assert.NotNull(item.NewStart);
        Assert.NotNull(item.NewEnd);
    }

    [Fact]
    public void IdempotencyKey_UsesMonthCaseAndProposal()
    {
        var command = MonthlyReviewService.BuildCorrectionCommand(
            Proposal(101, 202, At(7, 6), null), new ReviewMonth(2026, 7), "Benjamin");
        Assert.Equal("2026-07:case-1:77", command.IdempotencyKey);
    }

    [Fact]
    public void HikmatStartOnly_BuildsExactOriginalConcurrencyValues()
    {
        var proposal = Proposal(280389, 280389, At(8, 18), null);
        proposal.OriginalStart = At(8, 17);
        proposal.OriginalEnd = At(15, 29);
        proposal.FirstRecordOriginalStart = At(8, 17);
        proposal.FirstRecordOriginalEnd = At(15, 29);
        proposal.LastRecordOriginalStart = At(8, 17);
        proposal.LastRecordOriginalEnd = At(15, 29);
        proposal.FirstMainTaskExternalId = 30;
        proposal.LastMainTaskExternalId = 30;

        var command = MonthlyReviewService.BuildCorrectionCommand(
            proposal, new ReviewMonth(2026, 7), "Benjamin Tonoli");
        var item = Assert.Single(command.Corrections);

        Assert.Equal(280389, item.PerformanceId);
        Assert.Equal(new TimeSpan(8, 17, 0), item.OriginalStart);
        Assert.Equal(new TimeSpan(15, 29, 0), item.OriginalEnd);
        Assert.Equal(new TimeSpan(8, 18, 0), item.NewStart);
        Assert.Null(item.NewEnd);
        Assert.Equal(30, item.ExpectedMainTaskExternalId);
        Assert.Equal("CustomerWork", item.ExpectedActivityType);
        Assert.Equal("2026-07:case-1:77", command.IdempotencyKey);
    }

    [Theory]
    [InlineData("Travel")]
    [InlineData("WaitingTime")]
    [InlineData("NonLocationWork")]
    public void NonLocationPerformances_AreRejected(string activityType)
    {
        var snapshot = new MonthlyReviewService.PerformanceSnapshot(At(7, 5), At(8, 0), activityType, 1);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyReviewService.EnsureLocationBound(snapshot, 101, true));
    }

    [Fact]
    public void LocationBoundPerformance_IsAccepted()
    {
        var snapshot = new MonthlyReviewService.PerformanceSnapshot(At(7, 5), At(8, 0), "CustomerWork", 1);
        MonthlyReviewService.EnsureLocationBound(snapshot, 101, true);
    }

    private static DailyCorrectionProposal Proposal(
        long firstId,
        long lastId,
        DateTimeOffset? proposedStart,
        DateTimeOffset? proposedEnd) => new()
        {
            Id = 77,
            CaseId = "case-1",
            OriginalStart = At(7, 5),
            OriginalEnd = At(15, 35),
            ProposedStart = proposedStart,
            ProposedEnd = proposedEnd,
            Reason = "AdministrativeEntryError",
            ProposedBy = "Benjamin",
            CreatedAt = At(16, 0),
            Status = CorrectionProposalStatuses.Approved,
            FirstPerformanceId = firstId,
            LastPerformanceId = lastId,
            FirstActivityType = "CustomerWork",
            LastActivityType = "CustomerWork",
            FirstMainTaskExternalId = 11,
            LastMainTaskExternalId = 22,
            FirstRecordOriginalStart = At(7, 5),
            FirstRecordOriginalEnd = At(8, 0),
            LastRecordOriginalStart = At(10, 0),
            LastRecordOriginalEnd = At(15, 35),
        };

    private static PlenionCorrectionItem Item(long id, TimeSpan? start, TimeSpan? end) =>
        new(id, TimeSpan.FromHours(6), TimeSpan.FromHours(16), start, end,
            "CustomerWork", 1);

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 7, 9, hour, minute, 0, TimeSpan.FromHours(2));

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}

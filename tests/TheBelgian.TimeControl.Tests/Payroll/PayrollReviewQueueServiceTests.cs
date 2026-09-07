using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollReviewQueueServiceTests
{
    [Fact]
    public async Task Bulk_UpdatesOnlyExplicitCaseKeys_AndWritesAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (keyA, keyB, keyC) = await fixture.SeedThreeOpenCasesAsync();

        var result = await fixture.Service.BulkSetCaseStatusAsync(
            2026,
            8,
            [keyA, keyB],
            PayrollFindingStatus.NeedsFollowUp,
            "bulk follow-up",
            "Ada Admin",
            default);

        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Updated);
        Assert.Contains(keyA, result.UpdatedCaseKeys);
        Assert.Contains(keyB, result.UpdatedCaseKeys);
        Assert.DoesNotContain(keyC, result.UpdatedCaseKeys);

        await using var context = await fixture.Factory.CreateDbContextAsync();
        var findings = await context.PayrollFindingRecords.AsNoTracking().ToListAsync();
        Assert.Equal(2, findings.Count(item => item.Status == PayrollFindingStatus.NeedsFollowUp));
        Assert.Equal(1, findings.Count(item => item.Status == PayrollFindingStatus.Open));
        Assert.All(
            findings.Where(item => item.Status == PayrollFindingStatus.NeedsFollowUp),
            item =>
            {
                Assert.Equal("Ada Admin", item.ReviewedBy);
                Assert.Equal("bulk follow-up", item.ReviewComment);
                Assert.NotNull(item.ReviewedAtUtc);
            });

        var audits = await context.PayrollShadowReviewAudits.AsNoTracking().ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, item => Assert.Equal("Ada Admin", item.Actor));
        Assert.All(audits, item => Assert.Contains("queue-case:", item.Comment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bulk_Reviewed_RequiresComment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (keyA, _, _) = await fixture.SeedThreeOpenCasesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkSetCaseStatusAsync(
                2026,
                8,
                [keyA],
                PayrollFindingStatus.Reviewed,
                "   ",
                "Ada Admin",
                default));
    }

    [Fact]
    public async Task Bulk_RejectsResolved_NeverExecutesWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (keyA, _, _) = await fixture.SeedThreeOpenCasesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkSetCaseStatusAsync(
                2026,
                8,
                [keyA],
                PayrollFindingStatus.Resolved,
                "done",
                "Ada Admin",
                default));
        Assert.Contains("Bulk mag geen", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Actions.ExecuteCalls);
    }

    [Fact]
    public async Task CountsUpdateAfterDisposition_WithoutRebuildSemantics()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (keyA, _, _) = await fixture.SeedThreeOpenCasesAsync();

        var before = await fixture.Service.GetQueueAsync(
            2026,
            8,
            new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.All),
            default);
        Assert.Equal(3, before.Summary.Open);
        Assert.Equal(2, before.Summary.UnresolvedByCategory[PayrollReviewCategory.Project300]);

        await fixture.Service.SetCaseStatusAsync(
            2026,
            8,
            keyA,
            PayrollFindingStatus.Reviewed,
            "Intern werk bevestigd",
            "Ada Admin",
            default);

        var after = await fixture.Service.GetQueueAsync(
            2026,
            8,
            new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.All),
            default);
        Assert.Equal(2, after.Summary.Open);
        Assert.Equal(1, after.Summary.Reviewed);
        Assert.Equal(1, after.Summary.UnresolvedByCategory[PayrollReviewCategory.Project300]);
    }

    [Fact]
    public async Task GetQueue_DoesNotProposeActions()
    {
        await using var fixture = await Fixture.CreateAsync();
        _ = await fixture.SeedThreeOpenCasesAsync();
        _ = await fixture.Service.GetQueueAsync(
            2026,
            8,
            new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.Open),
            default);
        Assert.Equal(0, fixture.Actions.ProposeCalls);
        Assert.Equal(0, fixture.Actions.ExecuteCalls);
        Assert.Equal(1, fixture.Actions.ListCalls);
    }

    [Fact]
    public async Task GuidedDecision_PersistsDecisionCode_AndAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        _ = await fixture.SeedThreeOpenCasesAsync();
        var page = await fixture.Service.GetAdminQueueAsync(
            2026,
            8,
            new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.All),
            default);
        var adminKey = page.AdminCases.Single(item => item.FindingKeys.Contains("p300-a")).AdminCaseKey;

        var result = await fixture.Service.SetAdminDecisionAsync(
            2026,
            8,
            adminKey,
            PayrollGuidedDecisionCodes.P300WorkValid,
            null,
            "Ada Admin",
            default);

        Assert.Equal(PayrollFindingStatus.Reviewed, result.Status);
        Assert.Equal(PayrollGuidedDecisionCodes.P300WorkValid, result.DecisionCode);

        await using var context = await fixture.Factory.CreateDbContextAsync();
        var finding = await context.PayrollFindingRecords.AsNoTracking()
            .SingleAsync(item => item.FindingKey == "p300-a");
        Assert.Equal(PayrollGuidedDecisionCodes.P300WorkValid, finding.DecisionCode);
        Assert.Equal("Werk was terecht", finding.DecisionLabel);
        Assert.Equal(PayrollFindingStatus.Reviewed, finding.Status);

        var audit = await context.PayrollShadowReviewAudits.AsNoTracking()
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        Assert.Equal(PayrollGuidedDecisionCodes.P300WorkValid, audit.ReasonCode);
        Assert.Contains("admin-case:", audit.Comment, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Actions.ExecuteCalls);
    }

    [Fact]
    public async Task BulkAdminDecision_RejectsStandby()
    {
        await using var fixture = await Fixture.CreateAsync();
        _ = await fixture.SeedThreeOpenCasesAsync();
        var page = await fixture.Service.GetAdminQueueAsync(
            2026,
            8,
            new PayrollReviewQueueFilter(Category: PayrollReviewCategory.Standby, Scope: PayrollReviewQueueScope.All),
            default);
        var standby = Assert.Single(page.AdminCases);
        Assert.False(standby.AllowsBulkDisposition);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkSetAdminDecisionAsync(
                2026,
                8,
                [standby.AdminCaseKey],
                PayrollGuidedDecisionCodes.StandbyPhysicalOnly,
                "bulk",
                "Ada Admin",
                default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            TestFactory factory,
            PayrollReviewQueueService service,
            RecordingActionService actions)
        {
            _connection = connection;
            Factory = factory;
            Service = service;
            Actions = actions;
        }

        public TestFactory Factory { get; }
        public PayrollReviewQueueService Service { get; }
        public RecordingActionService Actions { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TimeControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var actions = new RecordingActionService();
            var service = new PayrollReviewQueueService(
                factory,
                actions,
                Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
                Options.Create(new PayrollActionsOptions { Enabled = true, ExecutionEnabled = false }),
                TimeProvider.System,
                NullLogger<PayrollReviewQueueService>.Instance);
            return new Fixture(connection, factory, service, actions);
        }

        public async Task<(string KeyA, string KeyB, string KeyC)> SeedThreeOpenCasesAsync()
        {
            await using var context = await Factory.CreateDbContextAsync();
            var month = new PayrollShadowMonth
            {
                Year = 2026,
                Month = 8,
                Status = PayrollShadowMonthStatus.InReview,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = "seed",
                PeriodStart = new DateOnly(2026, 8, 1),
                PeriodEnd = new DateOnly(2026, 8, 31),
                EvaluationDate = new DateOnly(2026, 9, 1),
            };
            context.PayrollShadowMonths.Add(month);
            await context.SaveChangesAsync();

            context.PayrollShadowEmployeeResults.AddRange(
                Emp(month.Id, "1", "Alpha"),
                Emp(month.Id, "2", "Bravo"),
                Emp(month.Id, "3", "Charlie"));

            var f1 = Finding(month.Id, PayrollFindingType.Project300WithoutPlanning, "p300-a", "1", new DateOnly(2026, 8, 1), 11);
            var f2 = Finding(month.Id, PayrollFindingType.Project300WithoutPlanning, "p300-b", "2", new DateOnly(2026, 8, 2), 22);
            var f3 = Finding(month.Id, PayrollFindingType.StandbyStartMismatch, "stb-c", "3", new DateOnly(2026, 8, 3), 33);
            context.PayrollFindingRecords.AddRange(f1, f2, f3);
            await context.SaveChangesAsync();

            return (
                PayrollReviewCaseBuilder.GroupKey(f1),
                PayrollReviewCaseBuilder.GroupKey(f2),
                PayrollReviewCaseBuilder.GroupKey(f3));
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        private static PayrollShadowEmployeeResult Emp(int monthId, string id, string name) => new()
        {
            ShadowMonthId = monthId,
            ResourceId = id,
            DisplayNameSnapshot = name,
            EligibilityStatus = PayrollEligibilityStatus.Included,
            ReviewStatus = PayrollEmployeeReviewStatus.Pending,
            AcertaIdentityStatus = AcertaIdentityStatus.Present,
        };

        private static PayrollFindingRecord Finding(
            int monthId,
            PayrollFindingType type,
            string key,
            string resourceId,
            DateOnly date,
            long perfId) =>
            new()
            {
                ShadowMonthId = monthId,
                FindingKey = key,
                ResourceId = resourceId,
                Date = date,
                FindingType = type,
                Severity = PayrollFindingSeverity.High,
                Status = PayrollFindingStatus.Open,
                Title = type.ToString(),
                Description = "d",
                Evidence = "e",
                SuggestedAction = "a",
                RelatedPerformanceIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { perfId }),
            };
    }

    private sealed class TestFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);
        public Task<TimeControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class RecordingActionService : IPayrollActionService
    {
        public int ListCalls { get; private set; }
        public int ProposeCalls { get; private set; }
        public int ExecuteCalls { get; private set; }

        public Task CancelAsync(Guid actionId, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PayrollActionExecutionResult> ExecuteAsync(
            Guid actionId, string comment, string actor, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            throw new InvalidOperationException("Queue must never execute.");
        }

        public Task<PayrollProposedActionRecord?> GetActionAsync(Guid actionId, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollProposedActionRecord?>(null);

        public Task<IReadOnlyList<PayrollProposedActionRecord>> ListActionsAsync(
            int year, int month, string? resourceId, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<PayrollProposedActionRecord>>([]);
        }

        public Task<PayrollActionConfirmationView?> PrepareConfirmationAsync(
            Guid actionId, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollActionConfirmationView?>(null);

        public Task<IReadOnlyList<PayrollProposedActionRecord>> ProposeFromFindingsAsync(
            int year, int month, string? resourceId, string actor, CancellationToken cancellationToken)
        {
            ProposeCalls++;
            return Task.FromResult<IReadOnlyList<PayrollProposedActionRecord>>([]);
        }
    }
}

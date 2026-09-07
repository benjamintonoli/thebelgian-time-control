using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Actions;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollActionServiceTests
{
    [Fact]
    public async Task Propose_AyrtonLikeGpsTravel_IsBlocked()
    {
        await using var fx = await Fixture.CreateAsync();
        var finding = await fx.SeedMissingTechFindingAsync(
            resourceId: "388",
            date: new DateOnly(2026, 8, 31),
            severity: PayrollFindingSeverity.High,
            gpsClass: nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            start: new DateTimeOffset(2026, 8, 31, 8, 35, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 8, 31, 10, 6, 0, TimeSpan.Zero),
            hours: 1.51m,
            projectId: "65274",
            bonNr: "26601949");

        var actions = await fx.Service.ProposeFromFindingsAsync(2026, 8, "388", "tester", default);
        var action = Assert.Single(actions);
        Assert.Equal(finding.FindingKey, action.FindingKey);
        Assert.Equal(PayrollProposedActionStatus.Blocked, action.Status);
        Assert.Contains("reis-naar-werf", action.BlockReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Propose_WhenMatchingPerformanceAppears_MarksReadyCreateStale()
    {
        await using var fx = await Fixture.CreateAsync();
        var finding = await fx.SeedMissingTechFindingAsync(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.High,
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1");

        // Seed a Ready create that was previously approved for review.
        var ready = await fx.SeedReadyCreateActionAsync(finding, mainTaskId: 7);
        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, ready.Status);

        fx.PerformanceSource.Rows =
        [
            Perf("100", new DateOnly(2026, 8, 10), 9001, "65274",
                new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)),
        ];

        var actions = await fx.Service.ProposeFromFindingsAsync(2026, 8, "100", "tester", default);
        var action = Assert.Single(actions, item => item.ActionId == ready.ActionId);
        Assert.Equal(PayrollProposedActionStatus.Stale, action.Status);
    }

    [Fact]
    public async Task Execute_Create_MockSuccess_Applied()
    {
        await using var fx = await Fixture.CreateAsync(executionEnabled: true, useMockWrites: true);
        var finding = await fx.SeedMissingTechFindingAsync(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.High,
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1");
        var ready = await fx.SeedReadyCreateActionAsync(finding, mainTaskId: 7);

        fx.CreateClient.ResponseFactory = cmd => new PlenionPerformanceCreateResponse(
            "success",
            "ok",
            "ref-1",
            cmd.IdempotencyKey,
            9_000_001,
            cmd.ResourceId,
            cmd.Date,
            cmd.Start,
            cmd.End,
            cmd.ProjectId,
            cmd.BonNr,
            cmd.MainTaskId);

        var result = await fx.Service.ExecuteAsync(ready.ActionId, "TimeControl payrollcontrole — ontbrekende prestatie", "tester", default);
        Assert.Equal(PayrollProposedActionStatus.Applied, result.Status);
        Assert.Equal(9_000_001, result.ResultPerformanceId);
        Assert.True(fx.Shadow.RebuildCalled);
    }

    [Fact]
    public async Task Execute_Create_ContractUnproven_FailedNotApplied()
    {
        await using var fx = await Fixture.CreateAsync(executionEnabled: true, useMockWrites: true);
        var finding = await fx.SeedMissingTechFindingAsync(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.High,
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1");
        var ready = await fx.SeedReadyCreateActionAsync(finding, mainTaskId: 7);
        fx.CreateClient.ResponseFactory = cmd => new PlenionPerformanceCreateResponse(
            "contract_unproven",
            "PWS performance-create contract is nog niet bewezen/actief; create geweigerd.",
            string.Empty,
            cmd.IdempotencyKey,
            null,
            cmd.ResourceId,
            cmd.Date,
            cmd.Start,
            cmd.End,
            cmd.ProjectId,
            cmd.BonNr,
            cmd.MainTaskId);

        var result = await fx.Service.ExecuteAsync(ready.ActionId, "reden", "tester", default);
        Assert.Equal(PayrollProposedActionStatus.Failed, result.Status);
        Assert.Contains("bewezen", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fx.Shadow.RebuildCalled);
    }

    [Fact]
    public async Task Execute_WhenExecutionDisabled_Throws()
    {
        await using var fx = await Fixture.CreateAsync(executionEnabled: false, useMockWrites: true);
        var finding = await fx.SeedMissingTechFindingAsync(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.High,
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1");
        var ready = await fx.SeedReadyCreateActionAsync(finding, mainTaskId: 7);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fx.Service.ExecuteAsync(ready.ActionId, "reden", "tester", default));
    }

    [Fact]
    public async Task IdempotentCreateRetry_ReturnsSamePerformance()
    {
        await using var fx = await Fixture.CreateAsync(executionEnabled: true, useMockWrites: true);
        var finding = await fx.SeedMissingTechFindingAsync(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.High,
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1");
        var ready = await fx.SeedReadyCreateActionAsync(finding, mainTaskId: 7);
        var calls = 0;
        fx.CreateClient.ResponseFactory = cmd =>
        {
            calls++;
            return new PlenionPerformanceCreateResponse(
                calls == 1 ? "success" : "already_applied",
                "ok",
                "ref-idem",
                cmd.IdempotencyKey,
                42,
                cmd.ResourceId,
                cmd.Date,
                cmd.Start,
                cmd.End,
                cmd.ProjectId,
                cmd.BonNr,
                cmd.MainTaskId);
        };

        var first = await fx.Service.ExecuteAsync(ready.ActionId, "reden", "tester", default);
        Assert.Equal(PayrollProposedActionStatus.Applied, first.Status);
        Assert.Equal(42, first.ResultPerformanceId);

        // Second execute on Applied should be rejected by status gate — prove client idempotency separately.
        fx.PerformanceSource.Rows =
        [
            Perf("100", new DateOnly(2026, 8, 10), 42, "65274",
                new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)),
        ];
        var secondClient = await fx.CreateClient.CreateAsync(
            new PlenionPerformanceCreateCommand(
                ready.ActionId.ToString("N"),
                "100",
                new DateOnly(2026, 8, 10),
                TimeSpan.FromHours(9),
                TimeSpan.FromHours(12),
                "65274",
                "BON1",
                7,
                "reden",
                "tester",
                ready.ActionId.ToString("N")),
            default);
        Assert.Equal("already_applied", secondClient.Status);
        Assert.Equal(42, secondClient.PerformanceId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Propose_StartAndEndSamePerformance_CreatesExactlyOneAdjustAction()
    {
        await using var fx = await Fixture.CreateAsync();
        var date = new DateOnly(2026, 8, 15);
        var start = new DateTimeOffset(2026, 8, 15, 17, 30, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 15, 19, 30, 0, TimeSpan.Zero);
        var proposedStart = new DateTimeOffset(2026, 8, 15, 18, 17, 0, TimeSpan.Zero);
        var proposedEnd = new DateTimeOffset(2026, 8, 15, 20, 7, 0, TimeSpan.Zero);
        const long perfId = 281765;

        fx.PerformanceSource.Rows =
        [
            new NormalizedPerformanceEntry(
                perfId,
                perfId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "100",
                date,
                start,
                end,
                2m,
                120m,
                TimeSpan.FromHours(2),
                new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
                null,
                23,
                "300",
                null,
                null,
                null,
                null,
                null,
                perfId,
                IsStandby: true),
        ];

        fx.GpsSource.Days =
        [
            new StandbyGpsDayEvidence(
                "100",
                date,
                true,
                false,
                "obj",
                "1-ABC",
                "mapped",
                [
                    new StandbyGpsTripEvidence(
                        "out",
                        proposedStart,
                        proposedStart.AddMinutes(30),
                        15m,
                        25,
                        "Thuisstraat 1, 1000 Brussel, België",
                        "Werf, 2000 Antwerpen, België",
                        "obj",
                        "1-ABC"),
                    new StandbyGpsTripEvidence(
                        "back",
                        proposedEnd.AddMinutes(-25),
                        proposedEnd,
                        15m,
                        25,
                        "Werf, 2000 Antwerpen, België",
                        "Thuisstraat 1, 1000 Brussel, België",
                        "obj",
                        "1-ABC"),
                ]),
        ];

        await fx.SeedStandbyMismatchAsync(
            "100",
            date,
            PayrollFindingType.StandbyStartMismatch,
            perfId,
            proposedStart,
            proposedEnd);
        await fx.SeedStandbyMismatchAsync(
            "100",
            date,
            PayrollFindingType.StandbyEndMismatch,
            perfId,
            proposedStart,
            proposedEnd);

        var actions = await fx.Service.ProposeFromFindingsAsync(2026, 8, "100", "tester", default);
        var adjust = Assert.Single(actions, item =>
            item.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime
            && item.Status != PayrollProposedActionStatus.Cancelled);
        Assert.Equal($"standby-adjust:100:{date:yyyyMMdd}:{perfId}", adjust.FindingKey);
        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, adjust.Status);
        Assert.Contains("WaitingTime", adjust.ProposalSnapshotJson, StringComparison.Ordinal);
    }

    private static NormalizedPerformanceEntry Perf(
        string resourceId,
        DateOnly date,
        long id,
        string projectId,
        DateTimeOffset start,
        DateTimeOffset end) =>
        new(
            id,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            resourceId,
            date,
            start,
            end,
            (decimal)(end - start).TotalHours,
            (decimal)(end - start).TotalMinutes,
            end - start,
            new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            null,
            1,
            projectId,
            null,
            null,
            null,
            null,
            null,
            id);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            IDbContextFactory<TimeControlDbContext> factory,
            PayrollActionService service,
            FakeShadow shadow,
            FakePerformanceSource performances,
            FakeCreateClient createClient,
            FakeStandbyGpsSource gpsSource)
        {
            _connection = connection;
            Factory = factory;
            Service = service;
            Shadow = shadow;
            PerformanceSource = performances;
            CreateClient = createClient;
            GpsSource = gpsSource;
        }

        public IDbContextFactory<TimeControlDbContext> Factory { get; }
        public PayrollActionService Service { get; }
        public FakeShadow Shadow { get; }
        public FakePerformanceSource PerformanceSource { get; }
        public FakeCreateClient CreateClient { get; }
        public FakeStandbyGpsSource GpsSource { get; }

        public static async Task<Fixture> CreateAsync(
            bool executionEnabled = true,
            bool useMockWrites = true)
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
                var month = new PayrollShadowMonth
                {
                    Year = 2026,
                    Month = 8,
                    PeriodStart = new DateOnly(2026, 8, 1),
                    PeriodEnd = new DateOnly(2026, 8, 31),
                    EvaluationDate = new DateOnly(2026, 9, 1),
                    Status = PayrollShadowMonthStatus.InReview,
                    CalculationVersion = "test",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    CreatedBy = "test",
                    ConfigurationSnapshotJson = "{}",
                };
                context.PayrollShadowMonths.Add(month);
                await context.SaveChangesAsync();
                context.PayrollShadowEmployeeResults.Add(new PayrollShadowEmployeeResult
                {
                    ShadowMonthId = month.Id,
                    ResourceId = "388",
                    DisplayNameSnapshot = "Ayrton",
                    ResourceCodeSnapshot = "388",
                    EligibilityStatus = PayrollEligibilityStatus.Included,
                    AcertaIdentityStatus = AcertaIdentityStatus.Present,
                    OrdinaryStatus = PayrollMonthCalculationStatus.Calculated,
                    StandbyStatus = PayrollMonthCalculationStatus.Calculated,
                    CityStatus = PayrollMonthCalculationStatus.Calculated,
                    KmStatus = PayrollMonthCalculationStatus.Calculated,
                    Code414Status = PayrollMonthCalculationStatus.Calculated,
                    ReviewStatus = PayrollEmployeeReviewStatus.Pending,
                });
                context.PayrollShadowEmployeeResults.Add(new PayrollShadowEmployeeResult
                {
                    ShadowMonthId = month.Id,
                    ResourceId = "100",
                    DisplayNameSnapshot = "Test",
                    ResourceCodeSnapshot = "100",
                    EligibilityStatus = PayrollEligibilityStatus.Included,
                    AcertaIdentityStatus = AcertaIdentityStatus.Present,
                    OrdinaryStatus = PayrollMonthCalculationStatus.Calculated,
                    StandbyStatus = PayrollMonthCalculationStatus.Calculated,
                    CityStatus = PayrollMonthCalculationStatus.Calculated,
                    KmStatus = PayrollMonthCalculationStatus.Calculated,
                    Code414Status = PayrollMonthCalculationStatus.Calculated,
                    ReviewStatus = PayrollEmployeeReviewStatus.Pending,
                });
                await context.SaveChangesAsync();
            }

            var shadow = new FakeShadow();
            var performances = new FakePerformanceSource();
            var createClient = new FakeCreateClient();
            var gpsSource = new FakeStandbyGpsSource();
            var service = new PayrollActionService(
                factory,
                shadow,
                performances,
                gpsSource,
                new MockPlenionCorrectionClient(),
                createClient,
                Options.Create(new PayrollActionsOptions
                {
                    Enabled = true,
                    ExecutionEnabled = executionEnabled,
                }),
                Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
                Options.Create(new TimeControlCorrectionWriteOptions
                {
                    Enabled = true,
                    UseMock = useMockWrites,
                    BaseUrl = "http://localhost",
                }),
                TimeProvider.System);
            return new Fixture(connection, factory, service, shadow, performances, createClient, gpsSource);
        }

        public async Task<PayrollFindingRecord> SeedStandbyMismatchAsync(
            string resourceId,
            DateOnly date,
            PayrollFindingType type,
            long performanceId,
            DateTimeOffset proposedStart,
            DateTimeOffset proposedEnd)
        {
            await using var context = await Factory.CreateDbContextAsync();
            var monthId = await context.PayrollShadowMonths.Select(item => item.Id).SingleAsync();
            var finding = new PayrollFindingRecord
            {
                ShadowMonthId = monthId,
                FindingKey = $"{type}:{resourceId}:{date:yyyyMMdd}:{performanceId}",
                ResourceId = resourceId,
                Date = date,
                FindingType = type,
                Severity = PayrollFindingSeverity.High,
                Status = PayrollFindingStatus.Open,
                Title = type.ToString(),
                Description = "desc",
                Evidence = "evidence",
                SuggestedAction = "act",
                RelatedPerformanceIdsJson = JsonSerializer.Serialize(new[] { performanceId }),
                SuggestedPayableStart = proposedStart,
                SuggestedPayableEnd = proposedEnd,
                GpsClassification = nameof(StandbyGpsClassification.PhysicalIntervention),
            };
            context.PayrollFindingRecords.Add(finding);
            await context.SaveChangesAsync();
            return finding;
        }

        public async Task<PayrollFindingRecord> SeedMissingTechFindingAsync(
            string resourceId,
            DateOnly date,
            PayrollFindingSeverity severity,
            string gpsClass,
            DateTimeOffset? start,
            DateTimeOffset? end,
            decimal? hours,
            string? projectId,
            string? bonNr)
        {
            await using var context = await Factory.CreateDbContextAsync();
            var monthId = await context.PayrollShadowMonths.Select(item => item.Id).SingleAsync();
            var finding = new PayrollFindingRecord
            {
                ShadowMonthId = monthId,
                FindingKey = $"missing-tech:1:{date:yyyyMMdd}:{resourceId}",
                ResourceId = resourceId,
                Date = date,
                FindingType = PayrollFindingType.MissingPlannedTechnicianPerformance,
                Severity = severity,
                Status = PayrollFindingStatus.Open,
                Title = "Mogelijk ontbrekende prestatie",
                Description = "desc",
                Evidence = "evidence",
                SuggestedAction = "act",
                RelatedPerformanceIdsJson = "[14]",
                SuggestedPayableStart = start,
                SuggestedPayableEnd = end,
                SuggestedPayableHours = hours,
                SuggestedProjectId = projectId,
                SuggestedBonNr = bonNr,
                GpsClassification = gpsClass,
            };
            context.PayrollFindingRecords.Add(finding);
            await context.SaveChangesAsync();
            return finding;
        }

        public async Task<PayrollProposedActionRecord> SeedReadyCreateActionAsync(
            PayrollFindingRecord finding,
            int mainTaskId)
        {
            var eligibility = PayrollActionEligibility.Evaluate(
                finding,
                new PayrollActionEligibilityContext(
                    IsEmployeeIncluded: true,
                    IsMonthFinalized: false,
                    HasMatchingExistingPerformance: false,
                    ProvenMainTaskId: mainTaskId,
                    ExistingPerformanceStart: null,
                    ExistingPerformanceEnd: null,
                    ExistingPerformanceId: null,
                    IntervalSemanticsOverride: PayrollIntervalSemantics.PayableWork));
            Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, eligibility.Status);
            await using var context = await Factory.CreateDbContextAsync();
            var monthId = await context.PayrollShadowMonths.Select(item => item.Id).SingleAsync();
            var action = new PayrollProposedActionRecord
            {
                ActionId = Guid.NewGuid(),
                ShadowMonthId = monthId,
                FindingKey = finding.FindingKey,
                FindingId = finding.Id,
                ResourceId = finding.ResourceId,
                ActionType = PayrollProposedActionType.CreateMissingPerformance,
                Status = PayrollProposedActionStatus.ReadyForApproval,
                EvidenceSnapshotJson = JsonSerializer.Serialize(eligibility.EvidenceSnapshot),
                ProposalSnapshotJson = JsonSerializer.Serialize(eligibility.CreateProposal),
                SourceRevision = eligibility.SourceRevision,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = "seed",
            };
            context.PayrollProposedActionRecords.Add(action);
            await context.SaveChangesAsync();
            return action;
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);

        public Task<TimeControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeShadow : IPayrollShadowService
    {
        public bool RebuildCalled { get; private set; }

        public Task<PayrollShadowMonth> RebuildSnapshotAsync(
            int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken)
        {
            RebuildCalled = true;
            return Task.FromResult(new PayrollShadowMonth
            {
                Id = 1,
                Year = year,
                Month = month,
                EvaluationDate = evaluationDate,
                Status = PayrollShadowMonthStatus.InReview,
            });
        }

        public Task<PayrollShadowMonth> CreateSnapshotAsync(int year, int month, DateOnly evaluationDate, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollMonthPeriodEligibilityInsight> GetPeriodEligibilityInsightAsync(int year, int month, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollMonthFinalizationBlockers> GetFinalizationBlockersAsync(int year, int month, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplyConfirmedRosterToMonthResult> ApplyConfirmedRosterToMonthAsync(int year, int month, string actor, string? comment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowMonth> FinalizeAsync(int year, int month, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(int year, int month, string? resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(int year, int month, string resourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(int year, int month, PayrollShadowEmployeeFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResetEligibilityAsync(SetPayrollEligibilityResetRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEligibilityAsync(SetPayrollEligibilityRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetReviewStatusAsync(SetPayrollReviewStatusRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollShadowMonth> StartReviewAsync(int year, int month, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayrollRosterPage> GetPayrollRosterAsync(PayrollRosterFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfirmPayrollRosterSelectionAsync(ConfirmPayrollRosterSelectionRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddManualPayrollEmployeeAsync(AddManualPayrollEmployeeRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePerformanceSource : IPayrollPerformanceSource
    {
        public List<NormalizedPerformanceEntry> Rows { get; set; } = [];

        public Task<IReadOnlyList<NormalizedPerformanceEntry>> ReadPerformancesAsync(
            DateOnly fromDate,
            DateOnly throughDate,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NormalizedPerformanceEntry>>(
                Rows.Where(item => resourceIds.Contains(item.ResourceId)).ToList());
    }

    private sealed class FakeStandbyGpsSource : IPayrollStandbyGpsSource
    {
        public List<StandbyGpsDayEvidence> Days { get; set; } = [];

        public Task<StandbyGpsBatchResult> ReadStandbyGpsAsync(
            DateOnly fromDate,
            DateOnly throughDate,
            IReadOnlyCollection<(string ResourceId, string DisplayName, DateOnly Date)> standbyResourceDates,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StandbyGpsBatchResult(Days, 0, 0, 0, Days.Count, 0, "test"));
    }

    private sealed class FakeCreateClient : IPlenionPerformanceCreateClient
    {
        public Func<PlenionPerformanceCreateCommand, PlenionPerformanceCreateResponse>? ResponseFactory { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<PlenionPerformanceCreateResponse> CreateAsync(
            PlenionPerformanceCreateCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(ResponseFactory?.Invoke(command)
                ?? new PlenionPerformanceCreateResponse(
                    "success", "ok", "ref", command.IdempotencyKey, 1,
                    command.ResourceId, command.Date, command.Start, command.End,
                    command.ProjectId, command.BonNr, command.MainTaskId));
    }
}

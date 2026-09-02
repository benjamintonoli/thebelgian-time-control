using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollShadowSnapshotTests
{
    [Fact]
    public async Task CreateSnapshot_StoresCalculatedValuesAndConfigurationContext()
    {
        await using var fixture = await Fixture.CreateAsync();
        var month = await fixture.Service.CreateSnapshotAsync(
            2026,
            7,
            new DateOnly(2026, 8, 1),
            "Ada Admin",
            default);

        Assert.Equal(PayrollShadowMonthStatus.ReadyForReview, month.Status);
        Assert.False(string.IsNullOrWhiteSpace(month.ConfigurationSnapshotJson));
        Assert.Contains("0.1448", month.ConfigurationSnapshotJson, StringComparison.Ordinal);

        var detail = await fixture.Service.GetMonthDetailAsync(
            2026,
            7,
            new PayrollShadowEmployeeFilter(),
            default);
        var included = Assert.Single(detail!.Employees);
        Assert.Equal(179m, included.LegacyTheoreticalHours);
        Assert.Equal(PayrollMonthCalculationStatus.Calculated,
            (await fixture.EmployeeAsync(month.Id, "1"))!.OrdinaryStatus);
    }

    [Fact]
    public async Task LaterConfigurationChanges_DoNotMutateFinalizedSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateSnapshotAsync(2026, 7, new DateOnly(2026, 8, 1), "Ada Admin", default);
        await fixture.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                "1",
                new DateOnly(2026, 7, 1),
                null,
                PayrollEligibilityStatus.Included,
                "Included",
                null),
            "Ada Admin",
            default);
        await fixture.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 7, "1", PayrollEmployeeReviewStatus.Accepted, null),
            "Ada Admin",
            default);
        await fixture.Service.StartReviewAsync(2026, 7, "Ada Admin", default);
        await fixture.Service.FinalizeAsync(2026, 7, "Ada Admin", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    "1",
                    new DateOnly(2026, 7, 15),
                    null,
                    PayrollEligibilityStatus.Excluded,
                    "LaterChange",
                    null),
                "Ada Admin",
                default));

        await fixture.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                "1",
                new DateOnly(2026, 8, 1),
                null,
                PayrollEligibilityStatus.Excluded,
                "FutureAllowed",
                null),
            "Ada Admin",
            default);

        var employee = await fixture.EmployeeAsync(
            (await fixture.MonthAsync())!.Id,
            "1");
        Assert.Equal(PayrollEligibilityStatus.Included, employee!.EligibilityStatus);
    }

    [Fact]
    public async Task FinalizedSnapshot_BlocksReviewEditsAndRecalculation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateSnapshotAsync(2026, 7, new DateOnly(2026, 8, 1), "Ada Admin", default);
        await fixture.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                "1",
                new DateOnly(2026, 7, 1),
                null,
                PayrollEligibilityStatus.Included,
                "Included",
                null),
            "Ada Admin",
            default);
        await fixture.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 7, "1", PayrollEmployeeReviewStatus.Accepted, null),
            "Ada Admin",
            default);
        await fixture.Service.StartReviewAsync(2026, 7, "Ada Admin", default);
        await fixture.Service.FinalizeAsync(2026, 7, "Ada Admin", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateSnapshotAsync(2026, 7, new DateOnly(2026, 8, 1), "Ada Admin", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetReviewStatusAsync(
                new SetPayrollReviewStatusRequest(2026, 7, "1", PayrollEmployeeReviewStatus.Pending, null),
                "Ada Admin",
                default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, TestFactory factory, PayrollShadowService service)
        {
            _connection = connection;
            Factory = factory;
            Service = service;
        }

        public TestFactory Factory { get; }
        public PayrollShadowService Service { get; }

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

            var payrollOptions = Options.Create(new PayrollShadowOptions
            {
                Enabled = true,
                AdminUiEnabled = true,
            });
            var service = new PayrollShadowService(
                factory,
                new TestResourceReader(),
                new TestPerformanceSource(),
                new TestCalendarSource(),
                new PayrollShadowCalculationService(),
                payrollOptions,
                TimeProvider.System);
            return new Fixture(connection, factory, service);
        }

        public async Task<PayrollShadowMonth?> MonthAsync()
        {
            await using var context = await Factory.CreateDbContextAsync();
            return await context.PayrollShadowMonths.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Year == 2026 && item.Month == 7);
        }

        public async Task<PayrollShadowEmployeeResult?> EmployeeAsync(int monthId, string resourceId)
        {
            await using var context = await Factory.CreateDbContextAsync();
            return await context.PayrollShadowEmployeeResults.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ShadowMonthId == monthId && item.ResourceId == resourceId);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class TestFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);
        public Task<TimeControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestResourceReader : IPayrollResourceReader
    {
        public Task<IReadOnlyList<PayrollEmployeeCandidate>> ReadCandidatesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollEmployeeCandidate>>(
            [
                new(
                    "1",
                    "R1",
                    "Ada Admin",
                    "ada@example.test",
                    "1",
                    null,
                    null,
                    null,
                    1,
                    null,
                    AcertaIdentityStatus.Present),
            ]);
    }

    private sealed class TestPerformanceSource : IPayrollPerformanceSource
    {
        public Task<IReadOnlyList<NormalizedPerformanceEntry>> ReadPerformancesAsync(
            DateOnly fromDate,
            DateOnly throughDate,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default)
        {
            var date = new DateOnly(2026, 7, 2);
            return Task.FromResult<IReadOnlyList<NormalizedPerformanceEntry>>(
            [
                new(
                    1,
                    "1",
                    "1",
                    date,
                    new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2)),
                    new DateTimeOffset(date.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2)),
                    8m,
                    480m,
                    TimeSpan.FromHours(8),
                    new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
                    10m,
                    10,
                    "P1",
                    100,
                    null,
                    "Work",
                    null,
                    "1000",
                    1),
            ]);
        }
    }

    private sealed class TestCalendarSource : IPayrollCalendarSource
    {
        public Task<IReadOnlyList<PlenionCalendarRow>> ReadCalendarRowsAsync(
            DateOnly fromDate,
            DateOnly throughDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlenionCalendarRow>>([]);
    }
}

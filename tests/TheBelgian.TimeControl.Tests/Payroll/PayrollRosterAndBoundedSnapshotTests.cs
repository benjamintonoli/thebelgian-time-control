using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollRosterAndBoundedSnapshotTests
{
    [Fact]
    public async Task ViewingRoster_DoesNotCreateConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.ConfigCountAsync();
        var page = await fixture.Service.GetPayrollRosterAsync(new PayrollRosterFilter(), default);
        Assert.NotEmpty(page.Rows);
        Assert.Equal(before, await fixture.ConfigCountAsync());
        Assert.Contains(page.Rows, row => row.AutoSuggested && !row.HasExplicitConfiguration);
    }

    [Fact]
    public async Task ConfirmCheckedProposal_CreatesIncludedConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ConfirmPayrollRosterSelectionAsync(
            new ConfirmPayrollRosterSelectionRequest(
                new DateOnly(2026, 8, 1),
                ["10"],
                [],
                "RosterConfirmation",
                "confirm tech"),
            "Ada Admin",
            default);

        await using var context = await fixture.Factory.CreateDbContextAsync();
        var config = Assert.Single(await context.PayrollEmployeeConfigurationRecords.AsNoTracking().ToListAsync());
        Assert.Equal("10", config.ResourceId);
        Assert.Equal(PayrollEligibilityStatus.Included, config.EligibilityStatus);
        Assert.Equal("Ada Admin", config.CreatedBy);
        Assert.DoesNotContain("rijksreg", config.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmUncheckedAutoProposal_CreatesExcludedConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.ConfirmPayrollRosterSelectionAsync(
            new ConfirmPayrollRosterSelectionRequest(
                new DateOnly(2026, 8, 1),
                [],
                ["10"],
                "RosterConfirmation",
                "exclude tech"),
            "Ada Admin",
            default);

        await using var context = await fixture.Factory.CreateDbContextAsync();
        var config = Assert.Single(await context.PayrollEmployeeConfigurationRecords.AsNoTracking().ToListAsync());
        Assert.Equal(PayrollEligibilityStatus.Excluded, config.EligibilityStatus);
    }

    [Fact]
    public async Task ManualExtra_CreatesIncludedOutsideAutoSet()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.AddManualPayrollEmployeeAsync(
            new AddManualPayrollEmployeeRequest(
                "20",
                new DateOnly(2026, 8, 1),
                null,
                "ManualPayrollInclusion",
                "designer override"),
            "Ada Admin",
            default);

        var page = await fixture.Service.GetPayrollRosterAsync(
            new PayrollRosterFilter(PayrollRosterFilterKind.ManualExtras),
            default);
        var designer = Assert.Single(page.Rows);
        Assert.Equal("20", designer.ResourceId);
        Assert.False(designer.AutoSuggested);
        Assert.Equal(PayrollEligibilityStatus.Included, designer.EffectiveEligibility);
        Assert.Equal(PayrollRosterSource.ManualIncluded, designer.Source);
    }

    [Fact]
    public async Task Snapshot_UsesBoundedUniverse_NotAllResources()
    {
        await using var fixture = await Fixture.CreateAsync();
        var month = await fixture.Service.CreateSnapshotAsync(
            2026,
            8,
            new DateOnly(2026, 9, 2),
            "Ada Admin",
            default);
        var detail = await fixture.Service.GetMonthDetailAsync(
            2026,
            8,
            new PayrollShadowEmployeeFilter(),
            default);

        Assert.Single(detail!.Employees);
        Assert.Equal("10", detail.Employees[0].ResourceId);
        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, detail.Employees[0].EligibilityStatus);
        Assert.DoesNotContain(detail.Employees, item => item.ResourceId == "20");
        Assert.DoesNotContain(detail.Employees, item => item.ResourceId == "25");
        _ = month;
    }

    [Fact]
    public async Task ExplicitIncludedDesigner_EntersSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.AddManualPayrollEmployeeAsync(
            new AddManualPayrollEmployeeRequest(
                "20",
                new DateOnly(2026, 8, 1),
                null,
                "ManualPayrollInclusion",
                null),
            "Ada Admin",
            default);
        await fixture.Service.CreateSnapshotAsync(2026, 8, new DateOnly(2026, 9, 2), "Ada Admin", default);
        var detail = await fixture.Service.GetMonthDetailAsync(2026, 8, new PayrollShadowEmployeeFilter(), default);
        Assert.Contains(detail!.Employees, item => item.ResourceId == "20");
        Assert.Equal(
            PayrollEligibilityStatus.Included,
            detail.Employees.Single(item => item.ResourceId == "20").EligibilityStatus);
    }

    [Fact]
    public async Task ExplicitIncluded_MissingAcerta_EntersSnapshot_ButFinalizeBlocked()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.AddManualPayrollEmployeeAsync(
            new AddManualPayrollEmployeeRequest(
                "12",
                new DateOnly(2026, 8, 1),
                null,
                "ManualPayrollInclusion",
                "missing acerta override"),
            "Ada Admin",
            default);
        await fixture.Service.CreateSnapshotAsync(2026, 8, new DateOnly(2026, 9, 2), "Ada Admin", default);
        var detail = await fixture.Service.GetMonthDetailAsync(2026, 8, new PayrollShadowEmployeeFilter(), default);
        var missing = Assert.Single(detail!.Employees, item => item.ResourceId == "12");
        Assert.Equal(PayrollEligibilityStatus.Included, missing.EligibilityStatus);
        Assert.Equal(AcertaIdentityStatus.Missing, missing.AcertaIdentityStatus);

        await fixture.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 8, "12", PayrollEmployeeReviewStatus.Accepted, null),
            "Ada Admin",
            default);
        // Exclude other NeedsDecision so finalization gate is specifically Acerta.
        foreach (var employee in detail.Employees.Where(item =>
                     item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision))
        {
            await fixture.Service.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    employee.ResourceId,
                    new DateOnly(2026, 8, 1),
                    null,
                    PayrollEligibilityStatus.Excluded,
                    "TestExclude",
                    null),
                "Ada Admin",
                default);
        }

        await fixture.Service.StartReviewAsync(2026, 8, "Ada Admin", default);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.FinalizeAsync(2026, 8, "Ada Admin", default));
        Assert.Contains("Acerta", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectLeider_Task23_EntersSnapshot_AndFiltersOrdinaryRows()
    {
        await using var fixture = await Fixture.CreateAsync(includeProjectLeaderTask23: true);
        await fixture.Service.CreateSnapshotAsync(2026, 8, new DateOnly(2026, 9, 2), "Ada Admin", default);
        var detail = await fixture.Service.GetMonthDetailAsync(2026, 8, new PayrollShadowEmployeeFilter(), default);
        Assert.Contains(detail!.Employees, item => item.ResourceId == "30");

        // Ordinary Project Leider rows are filtered; only HFDTAAK 23 (standby) remains → ordinary hours ~0.
        var leader = detail.Employees.Single(item => item.ResourceId == "30");
        Assert.Equal(0m, leader.LegacyActualOrdinaryHours ?? 0m);
        Assert.True((leader.StandbyRoundedHours ?? 0m) > 0m);
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

        public static async Task<Fixture> CreateAsync(bool includeProjectLeaderTask23 = false)
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
                new TestPerformanceSource(includeProjectLeaderTask23),
                new TestCalendarSource(),
                new PayrollShadowCalculationService(),
                payrollOptions,
                TimeProvider.System);
            return new Fixture(connection, factory, service);
        }

        public async Task<int> ConfigCountAsync()
        {
            await using var context = await Factory.CreateDbContextAsync();
            return await context.PayrollEmployeeConfigurationRecords.CountAsync();
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
                new("10", "R10", "Ada Technician", null, null, null, null, "Technieker", 1, null, AcertaIdentityStatus.Present),
                new("12", "R12", "Vendor Transport", null, null, null, null, "Technieker", 1, null, AcertaIdentityStatus.Missing),
                new("20", "R20", "Dana Designer", null, null, null, null, "Designer", 1, null, AcertaIdentityStatus.Present),
                new("25", "R25", "Kevin (OA)", null, null, null, null, "Technieker", 1, null, AcertaIdentityStatus.Present),
                new("30", "R30", "Pat Leader", null, null, null, null, LegacyPayrollTechnicianFunctions.ProjectLeider, 1, null, AcertaIdentityStatus.Present),
                new("99", "R99", "Mgr", null, null, null, null, "Manager", 2, null, AcertaIdentityStatus.Present),
            ]);
    }

    private sealed class TestPerformanceSource(bool includeProjectLeaderTask23) : IPayrollPerformanceSource
    {
        public Task<IReadOnlyList<NormalizedPerformanceEntry>> ReadPerformancesAsync(
            DateOnly fromDate,
            DateOnly throughDate,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default)
        {
            var date = new DateOnly(2026, 8, 3);
            var rows = new List<NormalizedPerformanceEntry>();
            if (resourceIds.Contains("10"))
            {
                rows.Add(Perf(1, "10", date, 10, 8m));
            }

            if (includeProjectLeaderTask23 && resourceIds.Contains("30"))
            {
                rows.Add(Perf(2, "30", date, 10, 8m));
                rows.Add(Perf(3, "30", date, 23, 4m));
            }

            return Task.FromResult<IReadOnlyList<NormalizedPerformanceEntry>>(rows);
        }

        private static NormalizedPerformanceEntry Perf(
            long id,
            string resourceId,
            DateOnly date,
            int hfdTaakId,
            decimal hours) =>
            new(
                id,
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                resourceId,
                date,
                new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2)),
                new DateTimeOffset(date.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2)),
                hours,
                hours * 60m,
                TimeSpan.FromHours((double)hours),
                new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
                0m,
                hfdTaakId,
                "P1",
                100,
                null,
                "Work",
                null,
                "1000",
                id,
                IsStandby: hfdTaakId == 23);
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

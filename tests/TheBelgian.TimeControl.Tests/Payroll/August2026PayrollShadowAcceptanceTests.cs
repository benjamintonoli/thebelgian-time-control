using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollShadowAcceptanceInvariantTests
{
    [Fact]
    public void CalculationVersion_IsReproducibleAndNonEmpty()
    {
        var version = PayrollShadowConfigurationSnapshot.CurrentCalculationVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(
            PayrollShadowConfigurationSnapshot.IsReproducibleVersion(version),
            $"CalculationVersion '{version}' is not reproducible.");
        Assert.DoesNotContain("PASS_IDRIJKSREG", version, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0+", false)]
    [InlineData("1.0.0+abc", false)]
    [InlineData("1.0.0+e30d365abcdef", true)]
    public void IsReproducibleVersion_ClassifiesExpectedShapes(string? value, bool expected) =>
        Assert.Equal(expected, PayrollShadowConfigurationSnapshot.IsReproducibleVersion(value));

    [Fact]
    public void ConfigurationSnapshot_ContainsRatesAndFingerprints_NoPii()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 8, new DateOnly(2026, 9, 2));
        var json = PayrollShadowConfigurationSnapshot.Build(
            period,
            PayrollShadowConfigurationSnapshot.ResolveKmConfiguration(period),
            PayrollShadowConfigurationSnapshot.CreateCityConfiguration(period),
            0,
            PayrollShadowConfigurationSnapshot.ComputeEligibilityHash([]));

        Assert.Contains("0.1448", json, StringComparison.Ordinal);
        Assert.Contains("5.00", json, StringComparison.Ordinal);
        Assert.Contains("cityPostcodeSet", json, StringComparison.Ordinal);
        Assert.Contains("cityPostcodeSetHash", json, StringComparison.Ordinal);
        Assert.Contains("eligibilityConfigurationHash", json, StringComparison.Ordinal);
        Assert.Contains("calculationVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS_IDRIJKSREG", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rijksreg", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalizedAugust_BlocksOverlappingEligibility_AllowsFutureSeptember()
    {
        await using var fixture = await InvariantFixture.CreateAsync();
        await fixture.CreateFinalizedAugustWithOneIncludedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    "1",
                    new DateOnly(2026, 8, 15),
                    null,
                    PayrollEligibilityStatus.Excluded,
                    "OverlapAugust",
                    null),
                "Ada Admin",
                default));

        await fixture.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                "1",
                new DateOnly(2026, 9, 1),
                null,
                PayrollEligibilityStatus.Excluded,
                "FutureSeptember",
                null),
            "Ada Admin",
            default);

        await using var context = await fixture.Factory.CreateDbContextAsync();
        var future = await context.PayrollEmployeeConfigurationRecords.AsNoTracking()
            .SingleAsync(item => item.ReasonCode == "FutureSeptember");
        Assert.Equal(new DateOnly(2026, 9, 1), future.ValidFrom);

        var august = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .SingleAsync(item => item.ResourceId == "1");
        Assert.Equal(PayrollEligibilityStatus.Included, august.EligibilityStatus);
    }

    [Fact]
    public async Task ZeroIncluded_BlocksFinalization()
    {
        await using var fixture = await InvariantFixture.CreateAsync();
        await fixture.Service.CreateSnapshotAsync(2026, 8, new DateOnly(2026, 9, 2), "Ada Admin", default);
        await fixture.Service.StartReviewAsync(2026, 8, "Ada Admin", default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.FinalizeAsync(2026, 8, "Ada Admin", default));
        Assert.Contains("minstens één Included", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingAcertaIdentity_BlocksFinalizationForIncluded()
    {
        await using var fixture = await InvariantFixture.CreateAsync(missingAcerta: true);
        // Missing Acerta is not auto-proposed; explicit Included still forces candidacy.
        await fixture.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                "1",
                new DateOnly(2026, 8, 1),
                null,
                PayrollEligibilityStatus.Included,
                "IncludedMissingAcerta",
                null),
            "Ada Admin",
            default);
        await fixture.Service.CreateSnapshotAsync(2026, 8, new DateOnly(2026, 9, 2), "Ada Admin", default);
        var detail = await fixture.Service.GetMonthDetailAsync(2026, 8, new PayrollShadowEmployeeFilter(), default);
        Assert.Contains(detail!.Employees, item => item.ResourceId == "1");
        Assert.Equal(
            PayrollEligibilityStatus.Included,
            detail.Employees.Single(item => item.ResourceId == "1").EligibilityStatus);

        await fixture.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 8, "1", PayrollEmployeeReviewStatus.Accepted, null),
            "Ada Admin",
            default);
        await fixture.Service.StartReviewAsync(2026, 8, "Ada Admin", default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.FinalizeAsync(2026, 8, "Ada Admin", default));
        Assert.Contains("Acerta", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalizedSnapshot_LoadsStoredValuesWithoutRecalculation()
    {
        await using var fixture = await InvariantFixture.CreateAsync();
        await fixture.CreateFinalizedAugustWithOneIncludedAsync();
        var before = await fixture.Service.GetEmployeeDetailAsync(2026, 8, "1", default);
        Assert.NotNull(before);
        var theo = before!.Employee.LegacyTheoreticalHours;
        var km = before.Employee.KmAmount;
        var json = before.Month.ConfigurationSnapshotJson;

        var after = await fixture.Service.GetEmployeeDetailAsync(2026, 8, "1", default);
        Assert.Equal(theo, after!.Employee.LegacyTheoreticalHours);
        Assert.Equal(km, after.Employee.KmAmount);
        Assert.Equal(json, after.Month.ConfigurationSnapshotJson);
        Assert.Equal(PayrollShadowMonthStatus.Finalized, after.Month.Status);
    }

    private sealed class InvariantFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private InvariantFixture(
            SqliteConnection connection,
            TestFactory factory,
            PayrollShadowService service)
        {
            _connection = connection;
            Factory = factory;
            Service = service;
        }

        public TestFactory Factory { get; }
        public PayrollShadowService Service { get; }

        public static async Task<InvariantFixture> CreateAsync(bool missingAcerta = false)
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

            var service = new PayrollShadowService(
                factory,
                new TestResourceReader(missingAcerta),
                new TestPerformanceSource(),
                new TestCalendarSource(),
                new PayrollShadowCalculationService(),
                Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
                TimeProvider.System);
            return new InvariantFixture(connection, factory, service);
        }

        public async Task CreateFinalizedAugustWithOneIncludedAsync()
        {
            await Service.CreateSnapshotAsync(2026, 8, new DateOnly(2026, 9, 2), "Ada Admin", default);
            await Service.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    "1",
                    new DateOnly(2026, 8, 1),
                    null,
                    PayrollEligibilityStatus.Included,
                    "Included",
                    null),
                "Ada Admin",
                default);
            await Service.SetReviewStatusAsync(
                new SetPayrollReviewStatusRequest(2026, 8, "1", PayrollEmployeeReviewStatus.Accepted, null),
                "Ada Admin",
                default);
            await Service.StartReviewAsync(2026, 8, "Ada Admin", default);
            await Service.FinalizeAsync(2026, 8, "Ada Admin", default);
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

    private sealed class TestResourceReader(bool missingAcerta) : IPayrollResourceReader
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
                    "Technieker",
                    1,
                    null,
                    missingAcerta ? AcertaIdentityStatus.Missing : AcertaIdentityStatus.Present),
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
            var date = new DateOnly(2026, 8, 3);
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

public sealed class August2026PayrollShadowAcceptanceTests(ITestOutputHelper output)
{
    private static readonly string ProductionRoot = @"C:\Apps\TheBelgian.TimeControl";
    private static readonly DateOnly EvaluationDate = new(2026, 9, 2);

    [Fact]
    public async Task August2026_RealShadowAcceptance_AgainstIsolatedDb()
    {
        var repoRoot = FindRepoRoot();
        var dbPath = Path.Combine(
            repoRoot,
            "artifacts",
            "payroll-acceptance",
            "time-control-payroll-acceptance.db");
        AssertSafeAcceptanceDbPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        output.WriteLine($"ACCEPTANCE_DB={dbPath}");
        Assert.DoesNotContain(ProductionRoot, dbPath, StringComparison.OrdinalIgnoreCase);

        if (!TryCreateLiveSources(repoRoot, out var resourceReader, out var performanceSource, out var calendarSource))
        {
            output.WriteLine("SKIP: Plenion ODBC unavailable for August acceptance.");
            return;
        }

        var overviewPath = Path.Combine(repoRoot, "reference", "powerbi", "2026-08", "Prestaties augustus overzicht.csv");
        if (!File.Exists(overviewPath))
        {
            output.WriteLine("SKIP: August overview CSV missing.");
            return;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath);
        await using var harness = await AcceptanceHarness.CreateAsync(
            dbPath,
            resourceReader,
            performanceSource,
            calendarSource);

        var month = await harness.Service.CreateSnapshotAsync(
            2026,
            8,
            EvaluationDate,
            "Acceptance Admin",
            default);
        Assert.Equal(PayrollShadowMonthStatus.ReadyForReview, month.Status);
        Assert.True(PayrollShadowConfigurationSnapshot.IsReproducibleVersion(month.CalculationVersion));
        Assert.Contains("0.1448", month.ConfigurationSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("5", month.ConfigurationSnapshotJson, StringComparison.Ordinal);

        var detail = await harness.Service.GetMonthDetailAsync(
            2026,
            8,
            new PayrollShadowEmployeeFilter(),
            default);
        Assert.NotNull(detail);
        output.WriteLine(
            $"Snapshot resources={detail!.Employees.Count} NeedsDecision={detail.Summary.NeedsDecision} " +
            $"CalculationVersion={month.CalculationVersion}");

        var candidates = await resourceReader.ReadCandidatesAsync(default);
        var pbiIds = overview.Select(row => row.ResourceId).ToHashSet(StringComparer.Ordinal);
        var snapshotIds = detail.Employees.Select(row => row.ResourceId).ToHashSet(StringComparer.Ordinal);
        var candidateIds = candidates.Select(row => row.ResourceId).ToHashSet(StringComparer.Ordinal);
        var captured = pbiIds.Intersect(snapshotIds).Count();
        var missed = pbiIds.Except(snapshotIds).OrderBy(id => id, StringComparer.Ordinal).ToList();
        output.WriteLine(
            $"Population master={candidateIds.Count} snapshot={snapshotIds.Count} PBI={pbiIds.Count} " +
            $"captured={captured} missed={missed.Count} extras={snapshotIds.Except(pbiIds).Count()}");
        foreach (var id in missed)
        {
            var master = candidates.FirstOrDefault(item => item.ResourceId == id);
            output.WriteLine(
                $"MISSED {id} name={master?.DisplayName} functie={master?.Function} " +
                $"end={master?.EmploymentEndDate}");
        }

        Assert.True(
            snapshotIds.Count < candidateIds.Count,
            $"Expected bounded snapshot ({snapshotIds.Count}) < master resources ({candidateIds.Count}).");
        Assert.True(
            snapshotIds.Count < 200,
            $"Expected operational candidate size, got {snapshotIds.Count}.");
        Assert.True(captured >= 40, $"Expected high PBI recall, captured={captured} of {pbiIds.Count}.");

        Assert.Equal(detail.Employees.Count, detail.Summary.NeedsDecision);
        Assert.Equal(0, detail.Summary.Included);

        var comparison = CompareToPowerBi(detail, overview);
        output.WriteLine(
            $"PBI compare compared={comparison.Compared} exactOrPrecision={comparison.ExactOrPrecision} " +
            $"mismatch={comparison.Mismatch} unexplained={comparison.Unexplained} " +
            $"kmExact={comparison.KmExact} cityExact={comparison.CityExact} standbyExact={comparison.StandbyExact}");
        foreach (var line in comparison.Notes.Take(30))
        {
            output.WriteLine(line);
        }

        Assert.True(comparison.Compared >= 40, "Expected substantial August PBI intersection.");
        Assert.Equal(comparison.Compared, comparison.KmExact);
        Assert.True(
            comparison.Unexplained == 0,
            "Unexplained algorithm mismatches: " + string.Join("; ", comparison.Notes.Where(n => n.Contains("[UNEXPLAINED]"))));
        // City/hour differences vs refreshed PBI are classified; do not change calculators in 7C.

        var pick = PickWorkflowCandidates(detail.Employees, pbiIds);
        output.WriteLine(
            $"Workflow picks A={pick.A} B={pick.B} C={pick.C} D={pick.D}");

        await harness.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                pick.A,
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                PayrollEligibilityStatus.Included,
                "AcceptanceTemporaryInclude",
                "7C acceptance only"),
            "Acceptance Admin",
            default);
        await harness.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                pick.D,
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                PayrollEligibilityStatus.Excluded,
                "AcceptanceTemporaryExclude",
                "7C acceptance only"),
            "Acceptance Admin",
            default);
        await harness.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                pick.C,
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                PayrollEligibilityStatus.Included,
                "AcceptanceTemporaryIncludeFollowUp",
                "7C acceptance only"),
            "Acceptance Admin",
            default);

        await harness.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 8, pick.C, PayrollEmployeeReviewStatus.NeedsFollowUp, "follow-up"),
            "Acceptance Admin",
            default);

        var started = await harness.Service.StartReviewAsync(2026, 8, "Acceptance Admin", default);
        Assert.Equal(PayrollShadowMonthStatus.InReview, started.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.FinalizeAsync(2026, 8, "Acceptance Admin", default));

        await harness.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                pick.B,
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                PayrollEligibilityStatus.Excluded,
                "AcceptanceResolveNeedsDecision",
                "resolve blocker"),
            "Acceptance Admin",
            default);
        await harness.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 8, pick.A, PayrollEmployeeReviewStatus.Accepted, "ok"),
            "Acceptance Admin",
            default);
        await harness.Service.SetReviewStatusAsync(
            new SetPayrollReviewStatusRequest(2026, 8, pick.C, PayrollEmployeeReviewStatus.Accepted, "resolved"),
            "Acceptance Admin",
            default);

        // Exclude everyone else still NeedsDecision so finalization can succeed with Included A+C only.
        var open = await harness.Service.GetMonthDetailAsync(2026, 8, new PayrollShadowEmployeeFilter(), default);
        foreach (var employee in open!.Employees.Where(item =>
                     item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision))
        {
            await harness.Service.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    employee.ResourceId,
                    new DateOnly(2026, 8, 1),
                    new DateOnly(2026, 8, 31),
                    PayrollEligibilityStatus.Excluded,
                    "AcceptanceBulkExcludeRemainder",
                    "temporary acceptance isolation"),
                "Acceptance Admin",
                default);
        }

        var finalized = await harness.Service.FinalizeAsync(2026, 8, "Acceptance Admin", default);
        Assert.Equal(PayrollShadowMonthStatus.Finalized, finalized.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.SetReviewStatusAsync(
                new SetPayrollReviewStatusRequest(2026, 8, pick.A, PayrollEmployeeReviewStatus.Pending, null),
                "Acceptance Admin",
                default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.CreateSnapshotAsync(2026, 8, EvaluationDate, "Acceptance Admin", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    pick.A,
                    new DateOnly(2026, 8, 15),
                    null,
                    PayrollEligibilityStatus.Excluded,
                    "ShouldBlock",
                    null),
                "Acceptance Admin",
                default));
        await harness.Service.SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                pick.A,
                new DateOnly(2026, 9, 1),
                null,
                PayrollEligibilityStatus.Excluded,
                "FutureAllowed",
                null),
            "Acceptance Admin",
            default);

        var frozen = await harness.Service.GetEmployeeDetailAsync(2026, 8, pick.A, default);
        Assert.Equal(PayrollEligibilityStatus.Included, frozen!.Employee.EligibilityStatus);
        Assert.Equal(PayrollShadowMonthStatus.Finalized, frozen.Month.Status);

        var audits = await harness.Service.GetAuditTrailAsync(2026, 8, null, default);
        Assert.Contains(audits, item => item.Action == PayrollShadowAuditAction.MonthFinalized);
        Assert.Contains(audits, item => item.Action == PayrollShadowAuditAction.MonthReviewStarted);
        Assert.All(audits, item => Assert.Equal("Acceptance Admin", item.Actor));
        Assert.DoesNotContain(
            audits.SelectMany(item => new[] { item.Comment, item.ReasonCode, item.ResourceId }),
            value => value is not null && value.Contains("rijksreg", StringComparison.OrdinalIgnoreCase));

        WriteReport(repoRoot, dbPath, month, detail, comparison, pick, audits.Count, candidates.Count, overview.Count);
    }

    private static void AssertSafeAcceptanceDbPath(string dbPath)
    {
        var full = Path.GetFullPath(dbPath);
        if (full.StartsWith(ProductionRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Acceptance DB path rejects production root: {full}");
        }
    }

    private static (string A, string B, string C, string D) PickWorkflowCandidates(
        IReadOnlyList<PayrollShadowEmployeeRow> employees,
        HashSet<string> powerBiResourceIds)
    {
        var withAcerta = employees
            .Where(item => item.AcertaIdentityStatus == AcertaIdentityStatus.Present)
            .Where(item => powerBiResourceIds.Contains(item.ResourceId))
            .Where(item => item.LegacyTheoreticalHours is not null)
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToList();
        Assert.True(withAcerta.Count >= 4, "Need at least 4 Present-Acerta PBI cohort candidates for acceptance workflow.");
        return (withAcerta[0].ResourceId, withAcerta[1].ResourceId, withAcerta[2].ResourceId, withAcerta[3].ResourceId);
    }

    private static ComparisonSummary CompareToPowerBi(
        PayrollShadowMonthDetail detail,
        IReadOnlyList<PowerBiOverviewRow> overview)
    {
        var byId = overview.ToDictionary(row => row.ResourceId, StringComparer.Ordinal);
        var notes = new List<string>();
        var compared = 0;
        var exactOrPrecision = 0;
        var mismatch = 0;
        var unexplained = 0;
        var kmExact = 0;
        var cityExact = 0;
        var standbyExact = 0;

        foreach (var employee in detail.Employees)
        {
            if (!byId.TryGetValue(employee.ResourceId, out var pbi))
            {
                continue;
            }

            compared++;
            var theoOk = HoursClose(employee.LegacyTheoreticalHours, pbi.TheoreticalHours, exact: true);
            var actualOk = HoursClose(employee.LegacyActualOrdinaryHours, pbi.TotalHours);
            var diffOk = HoursClose(employee.LegacyDifferenceHours, pbi.OvertimeHours);
            var hourOk = theoOk && actualOk && diffOk;
            var standbyOk = HoursClose(employee.StandbyRoundedHours, pbi.StandbyHours, exact: true);
            var expectedCityAmount = (pbi.CityTripUnits ?? 0m) * 5m;
            var cityOk = CurrencyClose(employee.CityAllowanceAmount, expectedCityAmount, exact: true);
            var kmOk = CurrencyClose(employee.KmAmount, pbi.KmAmount);
            var code414Ok = CurrencyClose(
                employee.Code414Amount,
                (employee.CityAllowanceAmount ?? 0m) + (employee.KmAmount ?? 0m));

            if (kmOk)
            {
                kmExact++;
            }

            if (cityOk)
            {
                cityExact++;
            }

            if (standbyOk)
            {
                standbyExact++;
            }

            if (hourOk && standbyOk && cityOk && kmOk && code414Ok)
            {
                exactOrPrecision++;
                continue;
            }

            mismatch++;
            var classification = ClassifyMismatch(hourOk, theoOk, actualOk, diffOk, standbyOk, cityOk, kmOk);
            if (classification == "UNEXPLAINED")
            {
                unexplained++;
            }

            notes.Add(
                $"{employee.ResourceId} {employee.DisplayName}: [{classification}] " +
                $"hourOk={hourOk} standbyOk={standbyOk} cityOk={cityOk} kmOk={kmOk} code414Ok={code414Ok} " +
                $"snapCity€={employee.CityAllowanceAmount} pbiCityUnits={pbi.CityTripUnits} " +
                $"snapDiff={employee.LegacyDifferenceHours} pbiOver={pbi.OvertimeHours} " +
                $"snapKm={employee.KmAmount} pbiKm={pbi.KmAmount}");
        }

        return new ComparisonSummary(
            compared,
            exactOrPrecision,
            mismatch,
            unexplained,
            kmExact,
            cityExact,
            standbyExact,
            notes);
    }

    private static string ClassifyMismatch(
        bool hourOk,
        bool theoOk,
        bool actualOk,
        bool diffOk,
        bool standbyOk,
        bool cityOk,
        bool kmOk)
    {
        if (!kmOk || !theoOk)
        {
            return "UNEXPLAINED";
        }

        if (!cityOk && hourOk && standbyOk)
        {
            return "SOURCE_CHANGED_SINCE_EXPORT_CITY";
        }

        if (!hourOk && actualOk == false && kmOk)
        {
            return "SOURCE_CHANGED_SINCE_EXPORT_HOURS";
        }

        if (!hourOk && kmOk)
        {
            return "CSV_PRECISION_OR_SOURCE_DRIFT_HOURS";
        }

        if (!standbyOk && hourOk && cityOk && kmOk)
        {
            return "CSV_PRECISION_STANDBY_NULL_ZERO";
        }

        if (!cityOk)
        {
            return "SOURCE_CHANGED_SINCE_EXPORT_CITY";
        }

        return "UNEXPLAINED";
    }

    private static bool HoursClose(decimal? left, decimal? right, bool exact = false)
    {
        var l = left ?? 0m;
        var r = right ?? 0m;
        var toleranceHours = exact ? 0.0000001m : (1m / 60m) + 0.001m;
        return Math.Abs(l - r) <= toleranceHours;
    }

    private static bool CurrencyClose(decimal? left, decimal? right, bool exact = false)
    {
        var l = left ?? 0m;
        var r = right ?? 0m;
        var tolerance = exact ? 0.0000001m : 0.01m;
        return Math.Abs(l - r) <= tolerance;
    }

    private static void WriteReport(
        string repoRoot,
        string dbPath,
        PayrollShadowMonth month,
        PayrollShadowMonthDetail detail,
        ComparisonSummary comparison,
        (string A, string B, string C, string D) pick,
        int auditCount,
        int resourcesMaster,
        int pbiReference)
    {
        var path = Path.Combine(repoRoot, "artifacts", "payroll-acceptance", "august-2026-acceptance-summary.txt");
        File.WriteAllText(
            path,
            $"""
            DB={dbPath}
            Period=2026-08-01..2026-08-31
            EvaluationDate={month.EvaluationDate:yyyy-MM-dd}
            CalculationVersion={month.CalculationVersion}
            ResourcesMaster={resourcesMaster}
            SnapshotCandidates={detail.Employees.Count}
            NeedsDecisionInitial={detail.Summary.NeedsDecision}
            PbiReference={pbiReference}
            PbiCaptured={comparison.Compared}
            Compared={comparison.Compared}
            ExactOrPrecision={comparison.ExactOrPrecision}
            Mismatch={comparison.Mismatch}
            Unexplained={comparison.Unexplained}
            KmExact={comparison.KmExact}
            CityExact={comparison.CityExact}
            StandbyExact={comparison.StandbyExact}
            WorkflowA={pick.A}
            WorkflowB={pick.B}
            WorkflowC={pick.C}
            WorkflowD={pick.D}
            AuditCount={auditCount}
            """);
    }

    private static bool TryCreateLiveSources(
        string repoRoot,
        out IPayrollResourceReader resourceReader,
        out IPayrollPerformanceSource performanceSource,
        out IPayrollCalendarSource calendarSource)
    {
        var connectionString = Environment.GetEnvironmentVariable("PLENION_ODBC")
            ?? "DSN=PlenionWriteLive;";
        try
        {
            using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
            probe.Open();
        }
        catch
        {
            resourceReader = null!;
            performanceSource = null!;
            calendarSource = null!;
            return false;
        }

        var options = Options.Create(new PlenionOptions { PlenionOdbc = connectionString });
        resourceReader = new PlenionPayrollResourceReader(
            options,
            NullLogger<PlenionPayrollResourceReader>.Instance);
        performanceSource = new PlenionPayrollReader(
            options,
            NullLogger<PlenionPayrollReader>.Instance);
        calendarSource = new PlenionPayrollCalendarReader(
            options,
            NullLogger<PlenionPayrollCalendarReader>.Instance);
        _ = repoRoot;
        return true;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TheBelgian.TimeControl.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    private sealed record ComparisonSummary(
        int Compared,
        int ExactOrPrecision,
        int Mismatch,
        int Unexplained,
        int KmExact,
        int CityExact,
        int StandbyExact,
        IReadOnlyList<string> Notes);

    private sealed class AcceptanceHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private AcceptanceHarness(
            SqliteConnection connection,
            PayrollShadowService service)
        {
            _connection = connection;
            Service = service;
        }

        public PayrollShadowService Service { get; }

        public static async Task<AcceptanceHarness> CreateAsync(
            string dbPath,
            IPayrollResourceReader resourceReader,
            IPayrollPerformanceSource performanceSource,
            IPayrollCalendarSource calendarSource)
        {
            var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TimeControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new FileFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var service = new PayrollShadowService(
                factory,
                resourceReader,
                performanceSource,
                calendarSource,
                new PayrollShadowCalculationService(),
                Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
                TimeProvider.System);
            return new AcceptanceHarness(connection, service);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class FileFactory(DbContextOptions<TimeControlDbContext> options)
        : IDbContextFactory<TimeControlDbContext>
    {
        public TimeControlDbContext CreateDbContext() => new(options);
        public Task<TimeControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

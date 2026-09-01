using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class July2026CurrentMonthlyReplayTests(ITestOutputHelper output)
{
    private static readonly (string Name, string ResourceId)[] Cohort =
    [
        ("Hussain Amiri", "495"),
        ("Rayn Buyl", "633"),
        ("Hamza Boudarssissi", "656"),
        ("Jonas Deklerck", "124"),
        ("Kevin Van Malderen", "171"),
        ("Ivo Van Breedam", "19"),
    ];

    private static readonly PayrollPeriodSnapshot JulyPeriod = PayrollPeriodSnapshot.ForMonth(
        2026,
        7,
        new DateOnly(2026, 8, 1));

    [Fact]
    public async Task July2026_CurrentMonthlyReplay_IsDiagnosticOnly()
    {
        var context = await TryCreateContextAsync();
        if (context is null)
        {
            return;
        }

        foreach (var (name, resourceId) in Cohort)
        {
            var monthly = CalculateCurrentMonthly(context, resourceId);
            var historical = context.Overview.SingleOrDefault(row => row.ResourceId == resourceId);
            var classification = historical is null
                ? "UNEXPLAINED"
                : Math.Abs(monthly.LegacyDifferenceHours!.Value - historical.OvertimeHours!.Value) <= 0.02m
                    ? "SAME"
                    : "SOURCE_CHANGED_AFTER_GOLDEN_MASTER";

            output.WriteLine(
                $"{name}: actual={monthly.LegacyActualOrdinaryHours:F4} theo={monthly.LegacyTheoreticalHours:F4} " +
                $"diff={monthly.LegacyDifferenceHours:F4} standby={monthly.StandbyRoundedHours} " +
                $"historical={historical?.OvertimeHours:F4} [{classification}]");
        }
    }

    private static PayrollMonthShadowResult CalculateCurrentMonthly(CurrentMonthlyContext context, string resourceId)
    {
        var performances = context.Performances.Where(row => row.ResourceId == resourceId).ToList();
        var synthetic = context.Synthetic.Where(row => row.ResourceId == resourceId).ToList();
        var ledger = CurrentPayrollLedgerBuilder.Build(performances, synthetic);
        var dailyInputs = CurrentPayrollLegacyAdapter.ToDailyInputs(ledger);

        var dailyResults = new List<LegacyDailyPayrollResult>();
        var standbyTotals = new Dictionary<DateOnly, decimal>();
        foreach (var dayGroup in dailyInputs.GroupBy(row => row.Date))
        {
            var day = LegacyDailyPayrollPipeline.CalculateDay(resourceId, dayGroup.Key, dayGroup.ToList());
            dailyResults.Add(day.DailyResult);
            standbyTotals[dayGroup.Key] = LegacyStandbyDailyCalculator.CalculateDailyTotal(dayGroup.ToList());
        }

        return LegacyMonthlyHoursPipeline.Calculate(JulyPeriod, resourceId, dailyResults, standbyTotals);
    }

    private async Task<CurrentMonthlyContext?> TryCreateContextAsync()
    {
        var repoRoot = FindRepoRoot();
        var overviewPath = Path.Combine(repoRoot, "reference", "powerbi", "2026-07", "Prestaties juli overzicht.csv");
        var connectionString = Environment.GetEnvironmentVariable("PLENION_ODBC")
            ?? TryReadConnectionString(repoRoot);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            output.WriteLine("SKIP: Plenion ODBC connection string unavailable.");
            return null;
        }

        try
        {
            using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
            await probe.OpenAsync();
        }
        catch (Exception exception)
        {
            output.WriteLine($"SKIP: Plenion ODBC unavailable ({exception.Message}).");
            return null;
        }

        var performanceReader = new PlenionPayrollReader(
            Options.Create(new PlenionOptions { PlenionOdbc = connectionString }),
            NullLogger<PlenionPayrollReader>.Instance);
        var calendarReader = new PlenionPayrollCalendarReader(
            Options.Create(new PlenionOptions { PlenionOdbc = connectionString }),
            NullLogger<PlenionPayrollCalendarReader>.Instance);

        var resourceIds = Cohort.Select(pair => pair.ResourceId).ToArray();
        var performances = await performanceReader.ReadPerformancesAsync(
            JulyPeriod.PeriodStart,
            JulyPeriod.PeriodEnd,
            resourceIds);
        var rawCalendar = await calendarReader.ReadCalendarRowsAsync(
            JulyPeriod.PeriodStart,
            JulyPeriod.PeriodEnd);
        var synthetic = LegacyCalendarSynthesis.Synthesize(
            rawCalendar,
            JulyPeriod.PeriodStart,
            JulyPeriod.PeriodEnd,
            resourceIds.ToHashSet(StringComparer.Ordinal));

        IReadOnlyList<PowerBiOverviewRow> overview = [];
        if (File.Exists(overviewPath))
        {
            overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath);
        }

        return new CurrentMonthlyContext(performances, synthetic, overview);
    }

    private static string? TryReadConnectionString(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "TheBelgian.TimeControl.Web", "appsettings.json"),
            Path.Combine(repoRoot, "src", "TheBelgian.TimeControl.Web", "appsettings.Development.json"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var json = File.ReadAllText(path);
            const string marker = "\"PlenionOdbc\"";
            var index = json.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var colon = json.IndexOf(':', index);
            var firstQuote = json.IndexOf('"', colon + 1);
            var secondQuote = json.IndexOf('"', firstQuote + 1);
            if (firstQuote >= 0 && secondQuote > firstQuote)
            {
                return json[(firstQuote + 1)..secondQuote];
            }
        }

        return "DSN=PlenionWriteLive;";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private sealed record CurrentMonthlyContext(
        IReadOnlyList<NormalizedPerformanceEntry> Performances,
        IReadOnlyList<CalendarSyntheticEntry> Synthetic,
        IReadOnlyList<PowerBiOverviewRow> Overview);
}

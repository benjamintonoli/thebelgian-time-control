using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class July2026CurrentCalendarReplayTests(ITestOutputHelper output)
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

    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly Through = new(2026, 7, 31);

    [Fact]
    public async Task July2026_CurrentLedgerDailyReplay_IsDiagnosticOnly()
    {
        var context = await TryCreateContextAsync();
        if (context is null)
        {
            return;
        }

        foreach (var (name, resourceId) in Cohort)
        {
            var performances = context.Performances.Where(row => row.ResourceId == resourceId).ToList();
            var synthetic = context.Synthetic
                .Where(row => row.ResourceId == resourceId)
                .ToList();
            var ledger = CurrentPayrollLedgerBuilder.Build(performances, synthetic);
            var dailyInputs = CurrentPayrollLegacyAdapter.ToDailyInputs(ledger);

            decimal currentTotal = 0m;
            var resourceDays = 0;
            foreach (var dayGroup in dailyInputs.GroupBy(row => row.Date))
            {
                resourceDays++;
                var day = LegacyDailyPayrollPipeline.CalculateDay(
                    resourceId,
                    dayGroup.Key,
                    dayGroup.ToList());
                currentTotal += day.DailyResult.FinalDailyTotalHours;
            }

            var historicalTotal = context.HistoricalDetail
                .Where(row => row.ResourceId == resourceId && row.TotalHours is not null)
                .GroupBy(row => row.Date)
                .Sum(group => group.Max(row => row.TotalHours!.Value));

            var classification = Math.Abs(currentTotal - historicalTotal) <= 0.02m
                ? "SAME_SOURCE_AND_PARITY"
                : "SOURCE_CHANGED_AFTER_GOLDEN_MASTER";

            output.WriteLine(
                $"{name}: real={performances.Count} synthetic={synthetic.Count} days={resourceDays} " +
                $"current={currentTotal:F4} historical={historicalTotal:F4} [{classification}]");
        }

        if (context.Synthetic.Count > 0)
        {
            ReportIvoJuly(context);
            ReportSyntheticDiagnostics(context);
        }
    }

    [Fact]
    public async Task July2026_HistoricalKlDiagnostic_ComparesWhenCurrentSourceExists()
    {
        var context = await TryCreateContextAsync();
        if (context is null)
        {
            return;
        }

        var historicalSynthetic = context.HistoricalDetail
            .Where(row => row.PerformanceId?.StartsWith("KL", StringComparison.Ordinal) == true)
            .ToList();
        var currentByKey = context.Synthetic.ToDictionary(row => row.StableSourceKey);

        var exact = 0;
        var changed = 0;
        var missing = 0;
        foreach (var row in historicalSynthetic)
        {
            var key = row.PerformanceId!.Trim().Trim('"');
            if (!currentByKey.TryGetValue(key, out var current))
            {
                missing++;
                continue;
            }

            if (row.HfdTaakId == current.HfdTaakId &&
                NearlyEqual(row.AtlHours, current.SyntheticHoursRaw))
            {
                exact++;
            }
            else
            {
                changed++;
            }
        }

        output.WriteLine(
            $"Historical KL rows={historicalSynthetic.Count} exact={exact} changed={changed} missing={missing}");
    }

    private void ReportIvoJuly(CurrentReplayContext context)
    {
        output.WriteLine("IVO July synthetic:");
        foreach (var entry in context.Synthetic.Where(row => row.ResourceId == "19").OrderBy(row => row.Date))
        {
            var historical = context.HistoricalDetail.FirstOrDefault(row =>
                row.PerformanceId == entry.StableSourceKey);
            output.WriteLine(
                $"{entry.Date:yyyy-MM-dd} {entry.CalendarSourceId} type={entry.TypTaakId} hfd={entry.HfdTaakId} " +
                $"hours={entry.SyntheticHoursRaw} historical={(historical is not null ? "yes" : "no")}");
        }
    }

    private void ReportSyntheticDiagnostics(CurrentReplayContext context)
    {
        output.WriteLine(
            $"Calendar diagnostics: raw={context.RawCalendarRows.Count} synthetic={context.Synthetic.Count} " +
            $"type3={context.Synthetic.Count(row => row.TypTaakId == 3)} " +
            $"type5={context.Synthetic.Count(row => row.TypTaakId == 5)} " +
            $"type8={context.Synthetic.Count(row => row.TypTaakId == 8)} " +
            $"half={context.Synthetic.Count(row => row.IsHalfDay)} " +
            $"full={context.Synthetic.Count(row => row.IsFullDay)}");
    }

    private static bool NearlyEqual(decimal? expected, decimal actual) =>
        expected is null
            ? actual == 0m
            : Math.Abs(expected.Value - actual) <= 0.001m;

    private async Task<CurrentReplayContext?> TryCreateContextAsync()
    {
        var repoRoot = FindRepoRoot();
        var detailPath = Path.Combine(repoRoot, "reference", "powerbi", "2026-07", "Prestaties juli detail.csv");
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
        var performances = await performanceReader.ReadPerformancesAsync(From, Through, resourceIds);
        var rawCalendar = await calendarReader.ReadCalendarRowsAsync(From, Through);
        var synthetic = LegacyCalendarSynthesis.Synthesize(
            rawCalendar,
            From,
            Through,
            resourceIds.ToHashSet(StringComparer.Ordinal));

        IReadOnlyList<PowerBiDetailRow> historicalDetail = [];
        if (File.Exists(detailPath))
        {
            historicalDetail = PowerBiGoldenMasterReader.ReadDetail(detailPath);
        }

        return new CurrentReplayContext(performances, rawCalendar, synthetic, historicalDetail);
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

    private sealed record CurrentReplayContext(
        IReadOnlyList<NormalizedPerformanceEntry> Performances,
        IReadOnlyList<PlenionCalendarRow> RawCalendarRows,
        IReadOnlyList<CalendarSyntheticEntry> Synthetic,
        IReadOnlyList<PowerBiDetailRow> HistoricalDetail);
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Diagnostic replay of legacy calculators against current Plenion rows.
/// Not a hard parity assertion; skips when ODBC or golden master is unavailable.
/// </summary>
public sealed class July2026CurrentPlenionLegacyReplayTests(ITestOutputHelper output)
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
    public async Task July2026_CurrentPlenion_LegacyReplay_IsDiagnosticOnly()
    {
        if (!TryCreateContext(out var context))
        {
            return;
        }

        var rawRows = await context.Reader.ReadRawRowsForDiagnosticsAsync(
            From,
            Through,
            Cohort.Select(pair => pair.ResourceId).ToArray(),
            CancellationToken.None);
        var normalized = PayrollPerformanceMapper.MapMany(rawRows);

        foreach (var (name, resourceId) in Cohort)
        {
            var historicalRows = context.GoldenMasterDetail
                .Where(row => row.ResourceId == resourceId)
                .ToList();
            var currentRows = normalized.Where(row => row.ResourceId == resourceId).ToList();

            var historicalOverlap = SumOverlap(historicalRows, HistoricalLegacyParityAdapter.ToOverlapInput, HistoricalLegacyParityAdapter.DefaultSortKey);
            var currentOverlap = SumOverlapFromNormalized(currentRows);

            var historicalTravel = SumTravel(historicalRows, HistoricalLegacyParityAdapter.ToTravelInput);
            var currentTravel = SumTravelFromNormalized(currentRows);

            var overlapClass = Classify(historicalOverlap, currentOverlap);
            var travelClass = Classify(historicalTravel.Start, currentTravel.Start);

            output.WriteLine(
                $"{name}: overlap historical={historicalOverlap:F4} current={currentOverlap:F4} [{overlapClass}] " +
                $"travelStart historical={historicalTravel.Start:F4} current={currentTravel.Start:F4} " +
                $"travelEnd historical={historicalTravel.End:F4} current={currentTravel.End:F4} " +
                $"extra15 historical={historicalTravel.Extra15:F4} current={currentTravel.Extra15:F4} [{travelClass}]");
        }
    }

    private static decimal SumOverlap(
        IReadOnlyList<PowerBiDetailRow> rows,
        Func<PowerBiDetailRow, long, LegacyOverlapPerformanceInput> toInput,
        Func<PowerBiDetailRow, long> sortKey)
    {
        decimal total = 0m;
        foreach (var dayGroup in rows.GroupBy(row => row.Date))
        {
            if (dayGroup.Key is null)
            {
                continue;
            }

            var inputs = dayGroup
                .Select(row => toInput(row, sortKey(row)))
                .ToList();
            total += LegacyOverlapCalculator.Calculate(inputs).Sum(result => result.OverlapHours);
        }

        return total;
    }

    private static decimal SumOverlapFromNormalized(IReadOnlyList<NormalizedPerformanceEntry> rows)
    {
        decimal total = 0m;
        foreach (var dayGroup in rows.GroupBy(row => row.Date))
        {
            var inputs = dayGroup
                .Select(row => new LegacyOverlapPerformanceInput(
                    row.SourceEntryId,
                    row.SortKey,
                    row.HfdTaakId,
                    row.Start,
                    row.End,
                    row.AtlHoursRaw))
                .ToList();
            total += LegacyOverlapCalculator.Calculate(inputs).Sum(result => result.OverlapHours);
        }

        return total;
    }

    private static (decimal Start, decimal End, decimal Extra15) SumTravel(
        IReadOnlyList<PowerBiDetailRow> rows,
        Func<PowerBiDetailRow, LegacyTravelPerformanceInput> toInput)
    {
        decimal start = 0m;
        decimal end = 0m;
        decimal extra15 = 0m;
        foreach (var dayGroup in rows.GroupBy(row => row.Date))
        {
            if (dayGroup.Key is null)
            {
                continue;
            }

            var inputs = dayGroup.Select(toInput).ToList();
            var day = LegacyTravelDerivation.CalculateDay(dayGroup.First().ResourceId, dayGroup.Key.Value, inputs);
            start += day.TravelStartDeductionHours;
            end += day.TravelEndDeductionHours;
            extra15 += day.Extra15TotalHours;
        }

        return (start, end, extra15);
    }

    private static (decimal Start, decimal End, decimal Extra15) SumTravelFromNormalized(
        IReadOnlyList<NormalizedPerformanceEntry> rows)
    {
        decimal start = 0m;
        decimal end = 0m;
        decimal extra15 = 0m;
        foreach (var dayGroup in rows.GroupBy(row => row.Date))
        {
            var inputs = dayGroup
                .Select(row => new LegacyTravelPerformanceInput(
                    row.SourceEntryId,
                    row.HfdTaakId,
                    row.Start?.TimeOfDay,
                    row.AtlHoursRaw))
                .ToList();
            var day = LegacyTravelDerivation.CalculateDay(dayGroup.First().ResourceId, dayGroup.Key, inputs);
            start += day.TravelStartDeductionHours;
            end += day.TravelEndDeductionHours;
            extra15 += day.Extra15TotalHours;
        }

        return (start, end, extra15);
    }

    private static string Classify(decimal historical, decimal current) =>
        Math.Abs(historical - current) <= 0.001m
            ? "SAME"
            : "SOURCE_CHANGED_AFTER_GOLDEN_MASTER";

    private bool TryCreateContext(out LocalReconciliationContext context)
    {
        var repoRoot = FindRepoRoot();
        var detailPath = Path.Combine(repoRoot, "reference", "powerbi", "2026-07", "Prestaties juli detail.csv");
        var connectionString = Environment.GetEnvironmentVariable("PLENION_ODBC")
            ?? TryReadConnectionString(repoRoot);

        if (!File.Exists(detailPath))
        {
            output.WriteLine("SKIP: golden master detail CSV not present.");
            context = default!;
            return false;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            output.WriteLine("SKIP: Plenion ODBC connection string unavailable.");
            context = default!;
            return false;
        }

        try
        {
            using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
            probe.Open();
        }
        catch (Exception exception)
        {
            output.WriteLine($"SKIP: Plenion ODBC unavailable ({exception.Message}).");
            context = default!;
            return false;
        }

        var reader = new PlenionPayrollReader(
            Options.Create(new PlenionOptions { PlenionOdbc = connectionString }),
            NullLogger<PlenionPayrollReader>.Instance);
        context = new LocalReconciliationContext(
            reader,
            PowerBiGoldenMasterReader.ReadDetail(detailPath));
        return true;
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

    private sealed record LocalReconciliationContext(
        PlenionPayrollReader Reader,
        IReadOnlyList<PowerBiDetailRow> GoldenMasterDetail);
}

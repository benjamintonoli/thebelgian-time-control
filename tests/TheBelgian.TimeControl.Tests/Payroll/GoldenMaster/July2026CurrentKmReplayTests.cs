using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Current-source JULY payroll-period KM + Code414 shadow diagnostics.
/// Reads only PeriodStart..PeriodEnd (intersected with CJ window), not Jan→EvaluationDate.
/// EvaluationDate = 2026-09-01 for reproducibility; does not assert PBI equality.
/// </summary>
public sealed class July2026CurrentKmReplayTests(ITestOutputHelper output)
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

    private static readonly DateOnly EvaluationDate = new(2026, 9, 1);

    private static readonly KmAllowanceConfiguration KmConfig =
        KmAllowanceConfiguration.Year2026Legacy;

    private static readonly CityAllowanceConfiguration CityConfig =
        CityAllowanceConfiguration.July2026Legacy;

    private static readonly PayrollPeriodSnapshot JulyPeriod =
        PayrollPeriodSnapshot.ForMonth(2026, 7, EvaluationDate);

    [Fact]
    public async Task July2026_CurrentSixPersonJulyPeriodKmAndCode414_IsDiagnosticOnly()
    {
        if (!TryCreateReader(out var reader, out _))
        {
            output.WriteLine("SKIP: Plenion ODBC unavailable.");
            return;
        }

        var overviewPath = Path.Combine(
            FindRepoRoot(),
            "reference",
            "powerbi",
            "2026-07",
            "Prestaties juli overzicht.csv");
        if (!File.Exists(overviewPath))
        {
            output.WriteLine("SKIP: overview CSV missing.");
            return;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath)
            .ToDictionary(row => row.ResourceId, StringComparer.Ordinal);

        // Period context only — not CJ_FirstDay..EvaluationDate.
        var performances = await reader.ReadPerformancesAsync(
            JulyPeriod.PeriodStart,
            JulyPeriod.PeriodEnd,
            Cohort.Select(pair => pair.ResourceId).ToArray(),
            CancellationToken.None);

        foreach (var (name, resourceId) in Cohort)
        {
            var periodRows = performances.Where(row => row.ResourceId == resourceId).ToList();
            var dailyInputs = periodRows.Select(CurrentPayrollLegacyAdapter.ToDailyInputFromPerformance).ToList();
            var km = LegacyKmAllowanceCalculator.Calculate(dailyInputs, JulyPeriod, KmConfig);

            var cityUnits = CalculateJulyCityUnits(periodRows);
            var cityAmount = cityUnits * CityConfig.TripAmount;

            var monthly = LegacyMonthlyHoursPipeline.Calculate(
                JulyPeriod,
                resourceId,
                [],
                new Dictionary<DateOnly, decimal>(),
                cityUnits,
                CityConfig,
                km);

            var historicalKm = overview[resourceId].KmAmount;
            var historicalImplied = historicalKm is null
                ? (decimal?)null
                : historicalKm.Value / KmConfig.RatePerKm + (overview[resourceId].Extra75Hours ?? 0m);
            var classification = Classify(km.KmAmount, historicalKm);

            output.WriteLine(
                $"{name}: rows={periodRows.Count} eligibleKm={km.EligibleKm:F2} " +
                $"extra75Raw={km.Extra75RawKm:F2} extra75Ytd={km.Extra75YtdHours:F6} " +
                $"net={km.NetKmLegacyQuantity:F4} kmAmount={km.KmAmount:F4} " +
                $"cityAmount={cityAmount:F2} code414={monthly.Code414Amount:F4} " +
                $"historicalPbiKm={historicalKm:F4} historicalImpliedKm={historicalImplied:F2} " +
                $"diffKmAmount={(km.KmAmount - (historicalKm ?? 0m)):F4} [{classification}]");

            Assert.Equal(PayrollMonthCalculationStatus.Calculated, monthly.KmStatus);
            Assert.Equal(PayrollMonthCalculationStatus.Calculated, monthly.CityStatus);
            Assert.Equal(PayrollMonthCalculationStatus.Calculated, monthly.Code414Status);
            Assert.Equal(cityAmount + km.KmAmount, monthly.Code414Amount);
        }
    }

    [Fact]
    public async Task July2026_Current53ResourceJulyPeriodKmSummary_IsDiagnosticOnly()
    {
        if (!TryCreateReader(out var reader, out var overviewResourceIds))
        {
            output.WriteLine("SKIP: Plenion ODBC unavailable.");
            return;
        }

        var overviewPath = Path.Combine(
            FindRepoRoot(),
            "reference",
            "powerbi",
            "2026-07",
            "Prestaties juli overzicht.csv");
        if (!File.Exists(overviewPath))
        {
            output.WriteLine("SKIP: overview CSV missing.");
            return;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath)
            .ToDictionary(row => row.ResourceId, StringComparer.Ordinal);

        var performances = await reader.ReadPerformancesAsync(
            JulyPeriod.PeriodStart,
            JulyPeriod.PeriodEnd,
            overviewResourceIds,
            CancellationToken.None);

        var nonzero = 0;
        var zero = 0;
        var negative = 0;
        decimal totalEligibleKm = 0m;
        decimal totalKmAmount = 0m;
        decimal totalCode414 = 0m;
        var diffs = new List<(string ResourceId, string Name, decimal Diff)>();

        foreach (var resourceId in overviewResourceIds)
        {
            var periodRows = performances.Where(row => row.ResourceId == resourceId).ToList();
            var dailyInputs = periodRows.Select(CurrentPayrollLegacyAdapter.ToDailyInputFromPerformance).ToList();
            var km = LegacyKmAllowanceCalculator.Calculate(dailyInputs, JulyPeriod, KmConfig);

            var cityUnits = CalculateJulyCityUnits(periodRows);
            var cityAmount = cityUnits * CityConfig.TripAmount;
            var code414 = cityAmount + km.KmAmount;

            if (km.KmAmount > 0m)
            {
                nonzero++;
            }
            else if (km.KmAmount < 0m)
            {
                negative++;
            }
            else
            {
                zero++;
            }

            totalEligibleKm += km.EligibleKm;
            totalKmAmount += km.KmAmount;
            totalCode414 += code414;

            var historical = overview[resourceId].KmAmount ?? 0m;
            diffs.Add((resourceId, overview[resourceId].Resource, km.KmAmount - historical));
        }

        output.WriteLine(
            $"53-resource JULY period summary: resources={overviewResourceIds.Length} nonzeroKm={nonzero} " +
            $"zeroKm={zero} negative={negative} totalEligibleKm={totalEligibleKm:F2} " +
            $"totalKmAmount={totalKmAmount:F2} totalCode414={totalCode414:F2}");
        foreach (var diff in diffs.OrderByDescending(item => Math.Abs(item.Diff)).Take(5))
        {
            output.WriteLine($"  topDiff {diff.ResourceId} {diff.Name}: delta={diff.Diff:F2}");
        }

        Assert.Equal(53, overviewResourceIds.Length);
    }

    private static int CalculateJulyCityUnits(IReadOnlyList<NormalizedPerformanceEntry> julyRows)
    {
        var total = 0;
        foreach (var dayGroup in julyRows.GroupBy(row => row.Date))
        {
            var travelInputs = dayGroup
                .Select(row => new LegacyTravelPerformanceInput(
                    row.SourceEntryId,
                    row.HfdTaakId,
                    row.Start?.TimeOfDay,
                    row.AtlHoursRaw))
                .ToList();
            var travelById = LegacyTravelDerivation.CalculateRows(travelInputs)
                .ToDictionary(result => result.PerformanceId);

            foreach (var row in dayGroup)
            {
                if (row.IsCalendarSynthetic)
                {
                    continue;
                }

                travelById.TryGetValue(row.SourceEntryId, out var travel);
                total += LegacyCityAllowanceRowCalculator.CalculateRowUnits(
                    row.Postcode,
                    travel?.IsDailyMinVan ?? false,
                    travel?.IsDailyMaxVan ?? false,
                    CityConfig);
            }
        }

        return total;
    }

    private static string Classify(decimal currentKmAmount, decimal? historicalKmAmount)
    {
        if (historicalKmAmount is null)
        {
            return "CURRENT_SOURCE_SHADOW";
        }

        if (Math.Abs(currentKmAmount - historicalKmAmount.Value) <= 0.01m)
        {
            return "SAME_OUTPUT";
        }

        return "SOURCE_CHANGED_AFTER_GOLDEN_MASTER";
    }

    private static bool TryCreateReader(
        out PlenionPayrollReader reader,
        out string[] overviewResourceIds)
    {
        overviewResourceIds = [];
        var repoRoot = FindRepoRoot();
        var overviewPath = Path.Combine(repoRoot, "reference", "powerbi", "2026-07", "Prestaties juli overzicht.csv");
        if (!File.Exists(overviewPath))
        {
            reader = null!;
            return false;
        }

        overviewResourceIds = PowerBiGoldenMasterReader.ReadOverview(overviewPath)
            .Select(row => row.ResourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var connectionString = Environment.GetEnvironmentVariable("PLENION_ODBC")
            ?? TryReadConnectionString(repoRoot);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            reader = null!;
            return false;
        }

        try
        {
            using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
            probe.Open();
        }
        catch
        {
            reader = null!;
            return false;
        }

        reader = new PlenionPayrollReader(
            Options.Create(new PlenionOptions { PlenionOdbc = connectionString }),
            NullLogger<PlenionPayrollReader>.Instance);
        return true;
    }

    private static string? TryReadConnectionString(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "src", "TheBelgian.TimeControl.Web", "appsettings.json");
        if (!File.Exists(path))
        {
            return "DSN=PlenionWriteLive;";
        }

        var json = File.ReadAllText(path);
        const string marker = "\"PlenionOdbc\"";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return "DSN=PlenionWriteLive;";
        }

        var colon = json.IndexOf(':', index);
        var firstQuote = json.IndexOf('"', colon + 1);
        var secondQuote = json.IndexOf('"', firstQuote + 1);
        return firstQuote >= 0 && secondQuote > firstQuote
            ? json[(firstQuote + 1)..secondQuote]
            : "DSN=PlenionWriteLive;";
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
}

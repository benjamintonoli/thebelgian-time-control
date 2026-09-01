using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class July2026LegacyCityParityTests(ITestOutputHelper output)
{
    private static readonly string OverviewPath = Path.Combine(
        FindRepoRoot(),
        "reference",
        "powerbi",
        "2026-07",
        "Prestaties juli overzicht.csv");

    private static readonly string DetailPath = Path.Combine(
        FindRepoRoot(),
        "reference",
        "powerbi",
        "2026-07",
        "Prestaties juli detail.csv");

    private static readonly PayrollPeriodSnapshot JulyPeriod = PayrollPeriodSnapshot.ForMonth(
        2026,
        7,
        new DateOnly(2026, 7, 31));

    private static readonly CityAllowanceConfiguration CityConfig =
        CityAllowanceConfiguration.July2026Legacy;

    private static readonly string[] AdditionalRowLevelResourceIds =
    [
        "22", "171", "124", "656", "495", "633", "609", "14", "499", "388",
    ];

    [Fact]
    public void July2026_AllResources_MonthlyCityAggregateParity()
    {
        var context = TryLoadContext();
        if (context is null)
        {
            return;
        }

        var exact = 0;
        var mismatches = new List<string>();
        foreach (var overview in context.Overview)
        {
            var calculatedUnits = HistoricalCityParityCalculator.CalculateMonthlyUnits(
                context.Detail,
                overview.ResourceId);
            var monthly = LegacyMonthlyHoursPipeline.Calculate(
                JulyPeriod,
                overview.ResourceId,
                [],
                new Dictionary<DateOnly, decimal>(),
                calculatedUnits,
                CityConfig);

            var expectedUnits = (int)(overview.CityTripUnits ?? 0m);
            if (calculatedUnits == expectedUnits
                && monthly.CityTripUnits == expectedUnits
                && monthly.CityAllowanceAmount == expectedUnits * 5m)
            {
                exact++;
            }
            else
            {
                mismatches.Add(
                    $"{overview.ResourceId} {overview.Resource}: expected={expectedUnits} calc={calculatedUnits} amount={monthly.CityAllowanceAmount}");
            }
        }

        output.WriteLine($"Monthly city aggregate: exact={exact}/{context.Overview.Count}");
        Assert.Empty(mismatches);
        Assert.Equal(53, exact);
    }

    [Fact]
    public void July2026_AllDetailRows_RowLevelCityParity()
    {
        var context = TryLoadContext();
        if (context is null)
        {
            return;
        }

        var exact = 0;
        var missingPostcode = 0;
        var mismatches = new List<string>();
        foreach (var dayGroup in context.Detail.GroupBy(row => (row.ResourceId, row.Date)))
        {
            var dayRows = dayGroup.ToList();
            var travelById = LegacyTravelDerivation.CalculateRows(
                    dayRows.Select(HistoricalLegacyParityAdapter.ToTravelInput).ToList())
                .ToDictionary(result => result.PerformanceId);

            foreach (var row in dayRows)
            {
                var performanceId = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
                travelById.TryGetValue(performanceId, out var travel);
                if (travel is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.Postcode))
                {
                    missingPostcode++;
                }

                var calculated = HistoricalCityParityCalculator.CalculateRowUnits(row, travel);
                var expected = (int)(row.CityTripUnits ?? 0m);
                if (calculated == expected)
                {
                    exact++;
                }
                else
                {
                    mismatches.Add(
                        $"{row.PerformanceId} {row.Resource} {row.Date:yyyy-MM-dd}: expected={expected} calc={calculated} postcode={row.Postcode}");
                }
            }
        }

        output.WriteLine(
            $"Row-level city parity: rows={context.Detail.Count} exact={exact} missingPostcode={missingPostcode} mismatches={mismatches.Count}");
        foreach (var mismatch in mismatches.Take(10))
        {
            output.WriteLine($"  {mismatch}");
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void July2026_CohortAndAdditionalResources_RowLevelSampleParity()
    {
        var context = TryLoadContext();
        if (context is null)
        {
            return;
        }

        var cohortResourceIds = new[]
        {
            "495", "633", "656", "124", "171", "19",
        };
        var resourceIds = cohortResourceIds
            .Concat(AdditionalRowLevelResourceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var mismatches = new List<string>();
        foreach (var resourceId in resourceIds)
        {
            var resourceRows = context.Detail.Where(row => row.ResourceId == resourceId).ToList();
            foreach (var dayGroup in resourceRows.GroupBy(row => row.Date))
            {
                var travelById = LegacyTravelDerivation.CalculateRows(
                        dayGroup.Select(HistoricalLegacyParityAdapter.ToTravelInput).ToList())
                    .ToDictionary(result => result.PerformanceId);
                foreach (var row in dayGroup)
                {
                    var performanceId = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
                    travelById.TryGetValue(performanceId, out var travel);
                    if (travel is null)
                    {
                        continue;
                    }

                    var calculated = HistoricalCityParityCalculator.CalculateRowUnits(row, travel);
                    var expected = (int)(row.CityTripUnits ?? 0m);
                    if (calculated != expected)
                    {
                        mismatches.Add($"{resourceId} {row.PerformanceId}: expected={expected} calc={calculated}");
                    }
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void July2026_MonthlyPipeline_KmAndCode414RemainNotCalculated()
    {
        var monthly = LegacyMonthlyHoursPipeline.Calculate(
            JulyPeriod,
            "495",
            [],
            new Dictionary<DateOnly, decimal>(),
            9,
            CityConfig);

        Assert.Equal(PayrollMonthCalculationStatus.Calculated, monthly.CityStatus);
        Assert.Equal(9, monthly.CityTripUnits);
        Assert.Equal(45m, monthly.CityAllowanceAmount);
        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, monthly.KmStatus);
        Assert.Null(monthly.KmAmount);
        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, monthly.Code414Status);
        Assert.Null(monthly.Code414Amount);
    }

    private static HistoricalCityContext? TryLoadContext()
    {
        if (!File.Exists(OverviewPath) || !File.Exists(DetailPath))
        {
            return null;
        }

        return new HistoricalCityContext(
            PowerBiGoldenMasterReader.ReadOverview(OverviewPath),
            PowerBiGoldenMasterReader.ReadDetail(DetailPath));
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

    private sealed record HistoricalCityContext(
        IReadOnlyList<PowerBiOverviewRow> Overview,
        IReadOnlyList<PowerBiDetailRow> Detail);
}

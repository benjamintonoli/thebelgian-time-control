using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class July2026LegacyMonthlyParityTests(ITestOutputHelper output)
{
    private const decimal MinuteToleranceHours = 1m / 60m;
    private const decimal BjornCsvPrecisionToleranceHours = 0.04m;

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

    [Fact]
    public async Task July2026_AllResources_MonthlyOrdinaryAndStandbyParity()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var exact = 0;
        var withinMinute = 0;
        var mismatches = new List<string>();
        foreach (var overview in context.Overview)
        {
            var monthly = CalculateHistoricalMonthly(context, overview.ResourceId);
            var theoDiff = Math.Abs(monthly.LegacyTheoreticalHours!.Value - (overview.TheoreticalHours ?? 0m));
            var actualDiff = Math.Abs(monthly.LegacyActualOrdinaryHours!.Value - (overview.TotalHours ?? 0m));
            var overDiff = Math.Abs(monthly.LegacyDifferenceHours!.Value - (overview.OvertimeHours ?? 0m));
            var maxDiff = Math.Max(actualDiff, overDiff);

            if (theoDiff > 0.001m)
            {
                mismatches.Add($"{overview.ResourceId} theoretical: pbi={overview.TheoreticalHours} calc={monthly.LegacyTheoreticalHours}");
                continue;
            }

            var dayCount = context.Detail.Count(row => row.ResourceId == overview.ResourceId && row.Date is not null);
            var dayGroups = dayCount == 0
                ? 0
                : context.Detail
                    .Where(row => row.ResourceId == overview.ResourceId && row.Date is not null)
                    .Select(row => row.Date!.Value)
                    .Distinct()
                    .Count();
            var tolerance = overview.ResourceId == "499"
                ? BjornCsvPrecisionToleranceHours
                : dayGroups * MinuteToleranceHours;

            if (maxDiff <= tolerance)
            {
                if (maxDiff == 0m)
                {
                    exact++;
                }
                else
                {
                    withinMinute++;
                }
            }
            else
            {
                mismatches.Add(
                    $"{overview.ResourceId} {overview.Resource}: totDiffMin={(actualDiff * 60m):F2} overDiffMin={(overDiff * 60m):F2}");
            }
        }

        output.WriteLine($"Monthly ordinary: exact={exact} within1min={withinMinute} mismatches={mismatches.Count}");
        foreach (var mismatch in mismatches.Take(10))
        {
            output.WriteLine($"  {mismatch}");
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task July2026_StandbyResources_ExactParity()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var standbyResources = context.Overview
            .Where(row => row.StandbyHours is > 0)
            .ToList();
        Assert.True(standbyResources.Count >= 6);

        foreach (var overview in standbyResources)
        {
            var monthly = CalculateHistoricalMonthly(context, overview.ResourceId);
            Assert.Equal(overview.StandbyHours, monthly.StandbyRoundedHours);
            output.WriteLine(
                $"{overview.Resource}: raw={monthly.StandbyExactHours:F4} rounded={monthly.StandbyRoundedHours} pbi={overview.StandbyHours}");
        }
    }

    [Fact]
    public async Task July2026_Code135ShadowCandidateCounts()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var positive150 = 0;
        var zero150 = 0;
        var negative150 = 0;
        var nonzero200 = 0;
        foreach (var overview in context.Overview)
        {
            var monthly = CalculateHistoricalMonthly(context, overview.ResourceId);
            var units150 = monthly.Code135At150!.CalculatedUnits;
            if (units150 > 0m)
            {
                positive150++;
            }
            else if (units150 < 0m)
            {
                negative150++;
            }
            else
            {
                zero150++;
            }

            if (monthly.Code135At200!.CalculatedUnits > 0m)
            {
                nonzero200++;
            }
        }

        output.WriteLine(
            $"Code135 shadow: +150={positive150} zero150={zero150} -150={negative150} nonzero200={nonzero200}");
        Assert.Equal(context.Overview.Count, positive150 + zero150 + negative150);
        Assert.True(nonzero200 >= 6);
    }

    [Fact]
    public async Task July2026_Bjorn499_ClassifiedAsCsvPrecision()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var overview = context.Overview.Single(row => row.ResourceId == "499");
        var monthly = CalculateHistoricalMonthly(context, "499");
        var actualDiff = monthly.LegacyActualOrdinaryHours!.Value - overview.TotalHours!.Value;
        output.WriteLine($"Bjorn actual diff minutes={actualDiff * 60m:F2}");
        Assert.True(Math.Abs(actualDiff) <= BjornCsvPrecisionToleranceHours);
        Assert.True(Math.Abs(actualDiff) > 0m);
    }

    private static PayrollMonthShadowResult CalculateHistoricalMonthly(
        HistoricalMonthlyContext context,
        string resourceId)
    {
        var dailyResults = new List<LegacyDailyPayrollResult>();
        var standbyTotals = new Dictionary<DateOnly, decimal>();
        foreach (var dayGroup in context.Detail.Where(row => row.ResourceId == resourceId).GroupBy(row => row.Date))
        {
            if (dayGroup.Key is null)
            {
                continue;
            }

            var day = CalculateHistoricalDay(context, resourceId, dayGroup.Key.Value, dayGroup.ToList());
            dailyResults.Add(day.DailyResult);
            standbyTotals[dayGroup.Key.Value] = CalculateHistoricalStandbyDay(dayGroup.ToList());
        }

        return LegacyMonthlyHoursPipeline.Calculate(JulyPeriod, resourceId, dailyResults, standbyTotals);
    }

    private static decimal CalculateHistoricalStandbyDay(IReadOnlyList<PowerBiDetailRow> dayRows)
    {
        var watchRows = dayRows.Where(row => row.HfdTaakId == 23).ToList();
        if (watchRows.Count == 0)
        {
            return 0m;
        }

        var atl = watchRows.Sum(row => row.AtlHours ?? 0m);
        var begin = watchRows.Max(row => row.TravelStartHours ?? 0m);
        var end = watchRows.Max(row => row.TravelEndHours ?? 0m);
        return atl + begin + end;
    }

    private static LegacyDailyPayrollDayContext CalculateHistoricalDay(
        HistoricalMonthlyContext context,
        string resourceId,
        DateOnly date,
        IReadOnlyList<PowerBiDetailRow> dayRows)
    {
        var provisionalRows = dayRows
            .Select(row => HistoricalLegacyParityAdapter.ToDailyInput(
                row,
                HistoricalLegacyParityAdapter.DefaultSortKey(row),
                km: 0m))
            .ToList();
        var travelResults = LegacyTravelDerivation.CalculateRows(
            provisionalRows.Select(row => new LegacyTravelPerformanceInput(
                row.PerformanceId,
                row.HfdTaakId,
                row.Start?.TimeOfDay,
                row.AtlHoursRaw)).ToList());
        var travelById = travelResults.ToDictionary(result => result.PerformanceId);

        var rows = dayRows
            .Select(row =>
            {
                var id = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
                travelById.TryGetValue(id, out var travel);
                var km = HistoricalKmResolver.ResolveHistoricalKm(
                    row.Extra75Km,
                    LookupPlenionKm(context, row.PerformanceId),
                    travel?.IsDailyMinVan ?? false,
                    travel?.IsDailyMaxVan ?? false);
                return HistoricalLegacyParityAdapter.ToDailyInput(
                    row,
                    HistoricalLegacyParityAdapter.DefaultSortKey(row),
                    km);
            })
            .ToList();

        var extra75Override = dayRows.ToDictionary(
            row => HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId),
            row => row.Extra75Km ?? 0m);

        var componentOverrides = new LegacyDailyComponentOverrides(
            dayRows.Max(row => row.TravelStartHours),
            dayRows.Max(row => row.TravelEndHours),
            dayRows.Max(row => row.PauseCorrectionHours));

        return LegacyDailyPayrollPipeline.CalculateDay(
            resourceId,
            date,
            rows,
            extra75Override,
            componentOverrides);
    }

    private static decimal? LookupPlenionKm(HistoricalMonthlyContext context, string? performanceId)
    {
        if (performanceId is null)
        {
            return null;
        }

        var trimmed = performanceId.Trim().Trim('"');
        if (!long.TryParse(trimmed, out var id))
        {
            return null;
        }

        return context.KmLookup.TryGetValue(id, out var km) ? km : null;
    }

    private async Task<HistoricalMonthlyContext?> TryLoadContextAsync()
    {
        if (!File.Exists(OverviewPath) || !File.Exists(DetailPath))
        {
            output.WriteLine("SKIP: golden master CSVs not present.");
            return null;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(OverviewPath);
        var detail = PowerBiGoldenMasterReader.ReadDetail(DetailPath);
        var resourceIds = overview.Select(row => row.ResourceId).Distinct().ToArray();
        var kmLookup = await HistoricalPlenionKmEnrichment.TryLoadJuly2026KmLookupAsync(
            resourceIds,
            CancellationToken.None);
        return new HistoricalMonthlyContext(overview, detail, kmLookup);
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

    private sealed record HistoricalMonthlyContext(
        IReadOnlyList<PowerBiOverviewRow> Overview,
        IReadOnlyList<PowerBiDetailRow> Detail,
        IReadOnlyDictionary<long, decimal?> KmLookup);
}

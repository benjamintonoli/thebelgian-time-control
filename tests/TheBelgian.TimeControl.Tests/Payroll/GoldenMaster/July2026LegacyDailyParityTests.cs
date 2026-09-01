using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class July2026LegacyDailyParityTests(ITestOutputHelper output)
{
    private const decimal MinuteToleranceHours = 1m / 60m;

    private static readonly string DetailPath = Path.Combine(
        FindRepoRoot(),
        "reference",
        "powerbi",
        "2026-07",
        "Prestaties juli detail.csv");

    private static readonly (string Name, string ResourceId)[] Cohort =
    [
        ("Hussain Amiri", "495"),
        ("Rayn Buyl", "633"),
        ("Hamza Boudarssissi", "656"),
        ("Jonas Deklerck", "124"),
        ("Kevin Van Malderen", "171"),
        ("Ivo Van Breedam", "19"),
    ];

    [Fact]
    public async Task July2026_HistoricalPauseCorrection_Parity()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var evaluated = 0;
        var exact = 0;
        var mismatches = new List<string>();
        foreach (var dayGroup in context.Detail.GroupBy(row => new { row.ResourceId, row.Date }))
        {
            if (dayGroup.Key.Date is null)
            {
                continue;
            }

            var day = CalculateHistoricalDay(context, dayGroup.Key.ResourceId, dayGroup.Key.Date.Value, dayGroup.ToList());
            var calculated = day.PauseResult.PauseCorrectionHours;
            var expected = dayGroup.Select(row => row.PauseCorrectionHours).Max() ?? 0m;
            evaluated++;
            if (NearlyEqual(expected, calculated))
            {
                exact++;
            }
            else
            {
                mismatches.Add($"{dayGroup.Key.ResourceId}/{dayGroup.Key.Date:yyyy-MM-dd}: pbi={expected} calc={calculated}");
            }
        }

        output.WriteLine($"Pause correction days={evaluated} exact={exact} mismatches={mismatches.Count}");
        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task July2026_HistoricalExtra75_Parity()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var evaluated = 0;
        var exact = 0;
        var mismatches = new List<string>();
        foreach (var dayGroup in context.Detail.GroupBy(row => new { row.ResourceId, row.Date }))
        {
            if (dayGroup.Key.Date is null)
            {
                continue;
            }

            var day = CalculateHistoricalDay(context, dayGroup.Key.ResourceId, dayGroup.Key.Date.Value, dayGroup.ToList());
            var byId = day.Extra75Results.ToDictionary(result => result.PerformanceId);
            foreach (var row in dayGroup)
            {
                evaluated++;
                var id = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
                var calculated = byId[id].Extra75Km;
                var expected = row.Extra75Km ?? 0m;
                if (NearlyEqual(expected, calculated))
                {
                    exact++;
                }
                else
                {
                    mismatches.Add($"{row.ResourceId}/{row.Date:yyyy-MM-dd}/{id}: pbi={expected} calc={calculated}");
                }
            }
        }

        output.WriteLine($"Extra75 rows={evaluated} exact={exact} mismatches={mismatches.Count}");
        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task July2026_HistoricalDailyTotal_Parity_CohortAndAllJuly()
    {
        var context = await TryLoadContextAsync();
        if (context is null)
        {
            return;
        }

        var cohortFailures = new List<string>();
        foreach (var (name, resourceId) in Cohort)
        {
            var stats = EvaluateResource(context, resourceId);
            output.WriteLine(
                $"{name}: days={stats.Days} pbi={stats.ExpectedTotal:F4} calc={stats.CalculatedTotal:F4} diffMin={(stats.CalculatedTotal - stats.ExpectedTotal) * 60m:F2} exact={stats.ExactDays} within1={stats.WithinOneMinuteDays} mismatch={stats.MismatchDays}");
            if (stats.MismatchDays > 0)
            {
                cohortFailures.Add($"{name}: mismatchDays={stats.MismatchDays}");
            }
        }

        var all = EvaluateAllJuly(context);
        output.WriteLine(
            $"ALL JULY: resources={all.Resources.Count} days={all.Days} pbi={all.ExpectedTotal:F4} calc={all.CalculatedTotal:F4} diffMin={(all.CalculatedTotal - all.ExpectedTotal) * 60m:F2} exact={all.ExactDays} within1={all.WithinOneMinuteDays} mismatch={all.MismatchDays} missing={all.MissingInputDays}");
        foreach (var mismatch in all.TopMismatches.Take(20))
        {
            output.WriteLine($"  {mismatch}");
        }

        Assert.Empty(cohortFailures);
        Assert.Equal(0, all.MismatchDays);
    }

    private static LegacyDailyPayrollDayContext CalculateHistoricalDay(
        HistoricalDailyContext context,
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

    private static DailyStats EvaluateResource(HistoricalDailyContext context, string resourceId)
    {
        var stats = new DailyStats();
        foreach (var dayGroup in context.Detail.Where(row => row.ResourceId == resourceId).GroupBy(row => row.Date))
        {
            if (dayGroup.Key is null)
            {
                continue;
            }

            EvaluateDay(context, dayGroup.Key.Value, resourceId, dayGroup.ToList(), stats);
        }

        return stats;
    }

    private static DailyStats EvaluateAllJuly(HistoricalDailyContext context)
    {
        var stats = new DailyStats();
        foreach (var dayGroup in context.Detail.GroupBy(row => new { row.ResourceId, row.Date }))
        {
            if (dayGroup.Key.Date is null)
            {
                continue;
            }

            stats.Resources.Add(dayGroup.Key.ResourceId);
            EvaluateDay(context, dayGroup.Key.Date.Value, dayGroup.Key.ResourceId, dayGroup.ToList(), stats);
        }

        return stats;
    }

    private static void EvaluateDay(
        HistoricalDailyContext context,
        DateOnly date,
        string resourceId,
        IReadOnlyList<PowerBiDetailRow> dayRows,
        DailyStats stats)
    {
        stats.Days++;
        var expected = dayRows.Where(row => row.TotalHours is not null).Select(row => row.TotalHours!.Value).DefaultIfEmpty().Max();
        if (dayRows.All(row => row.TotalHours is null))
        {
            stats.MissingInputDays++;
            return;
        }

        var calculated = CalculateHistoricalDay(context, resourceId, date, dayRows).DailyResult.FinalDailyTotalHours;
        stats.ExpectedTotal += expected;
        stats.CalculatedTotal += calculated;
        var diff = Math.Abs(calculated - expected);
        if (diff == 0m)
        {
            stats.ExactDays++;
        }
        else if (diff <= MinuteToleranceHours)
        {
            stats.WithinOneMinuteDays++;
        }
        else
        {
            stats.MismatchDays++;
            stats.TopMismatches.Add($"{resourceId}/{date:yyyy-MM-dd}: expected={expected} actual={calculated} diffMin={(calculated - expected) * 60m:F2}");
        }
    }

    private static decimal? LookupPlenionKm(HistoricalDailyContext context, string? performanceId)
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

    private static bool NearlyEqual(decimal expected, decimal actual) =>
        Math.Abs(expected - actual) <= MinuteToleranceHours;

    private async Task<HistoricalDailyContext?> TryLoadContextAsync()
    {
        if (!File.Exists(DetailPath))
        {
            output.WriteLine("SKIP: golden master detail CSV not present.");
            return null;
        }

        var detail = PowerBiGoldenMasterReader.ReadDetail(DetailPath);
        var resourceIds = detail.Select(row => row.ResourceId).Distinct().ToArray();
        var kmLookup = await HistoricalPlenionKmEnrichment.TryLoadJuly2026KmLookupAsync(
            resourceIds,
            CancellationToken.None);
        return new HistoricalDailyContext(detail, kmLookup);
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

    private sealed class DailyStats
    {
        public HashSet<string> Resources { get; } = new(StringComparer.Ordinal);
        public int Days { get; set; }
        public decimal ExpectedTotal { get; set; }
        public decimal CalculatedTotal { get; set; }
        public int ExactDays { get; set; }
        public int WithinOneMinuteDays { get; set; }
        public int MismatchDays { get; set; }
        public int MissingInputDays { get; set; }
        public List<string> TopMismatches { get; } = [];
    }

    private sealed record HistoricalDailyContext(
        IReadOnlyList<PowerBiDetailRow> Detail,
        IReadOnlyDictionary<long, decimal?> KmLookup);
}

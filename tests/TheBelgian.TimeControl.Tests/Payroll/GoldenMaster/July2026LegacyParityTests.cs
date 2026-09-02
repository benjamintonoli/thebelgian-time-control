using System.Globalization;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class July2026LegacyParityTests(ITestOutputHelper output)
{
    private static readonly string DetailPath = Path.Combine(
        FindRepoRoot(),
        "reference",
        "powerbi",
        "2026-07",
        "Prestaties juli detail.csv");

    private static readonly (string Name, string ResourceId, decimal ExpectedOverlapTotal)[] Cohort =
    [
        ("Hussain Amiri", "495", 3.583333333313931m),
        ("Rayn Buyl", "633", 0m),
        ("Hamza Boudarssissi", "656", 9.99999999976717m),
        ("Jonas Deklerck", "124", 0m),
        ("Kevin Van Malderen", "171", 0m),
        ("Ivo Van Breedam", "19", 0m),
    ];

    private const decimal HourTolerance = 0.02m;

    [Fact]
    public void July2026_HistoricalOverlap_Parity_RowLevel()
    {
        if (!TryLoadDetail(out var detail))
        {
            return;
        }

        var evaluated = 0;
        var exactMatches = 0;
        var mismatches = new List<string>();
        foreach (var dayGroup in detail.GroupBy(row => new { row.ResourceId, row.Date }))
        {
            if (dayGroup.Key.Date is null)
            {
                continue;
            }

            var inputs = dayGroup
                .Select(row => HistoricalLegacyParityAdapter.ToOverlapInput(
                    row,
                    HistoricalLegacyParityAdapter.DefaultSortKey(row)))
                .ToList();
            var byId = LegacyOverlapCalculator.Calculate(inputs)
                .ToDictionary(result => result.PerformanceId);

            foreach (var row in dayGroup.Where(row => row.DuplicateHours is not null))
            {
                evaluated++;
                var id = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
                var calculated = byId[id].OverlapHours;
                if (NearlyEqual(row.DuplicateHours, calculated))
                {
                    exactMatches++;
                }
                else
                {
                    mismatches.Add(
                        $"{row.ResourceId}/{row.Date:yyyy-MM-dd}/{id}: pbi={row.DuplicateHours} calc={calculated}");
                }
            }
        }

        output.WriteLine($"Row-level overlap: evaluated={evaluated} exact={exactMatches} mismatches={mismatches.Count}");
        foreach (var mismatch in mismatches.Take(10))
        {
            output.WriteLine($"  {mismatch}");
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void July2026_HistoricalOverlap_Parity_UsingIdProjPrestSortKey()
    {
        if (!TryLoadDetail(out var detail))
        {
            return;
        }

        var failures = new List<string>();
        foreach (var (name, resourceId, _) in Cohort)
        {
            var resourceRows = detail
                .Where(row => row.ResourceId == resourceId)
                .ToList();
            if (resourceRows.Count == 0)
            {
                output.WriteLine($"{name}: no detail rows in refreshed export.");
                continue;
            }

            var expectedTotal = resourceRows.Sum(row => row.DuplicateHours ?? 0m);
            var calculatedTotal = SumCalculatedOverlap(resourceRows);
            output.WriteLine(
                $"{name}: rows={resourceRows.Count} pbi={expectedTotal} calc={calculatedTotal} diff={calculatedTotal - expectedTotal}");

            if (Math.Abs(calculatedTotal - expectedTotal) > HourTolerance)
            {
                failures.Add($"{name}: overlap total diff={calculatedTotal - expectedTotal}");
            }
        }

        var allTotal = SumCalculatedOverlap(detail);
        var pbiAllTotal = detail.Sum(row => row.DuplicateHours ?? 0m);
        output.WriteLine($"ALL JULY: rows={detail.Count} pbi={pbiAllTotal} calc={allTotal} diff={allTotal - pbiAllTotal}");

        Assert.True(
            failures.Count == 0,
            string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void July2026_HistoricalTravel_Parity_PerRowAndDay()
    {
        if (!TryLoadDetail(out var detail))
        {
            return;
        }

        foreach (var (name, resourceId, _) in Cohort)
        {
            var resourceRows = detail.Where(row => row.ResourceId == resourceId).ToList();
            var mismatchCount = 0;
            foreach (var dayGroup in resourceRows.GroupBy(row => row.Date))
            {
                if (dayGroup.Key is null)
                {
                    continue;
                }

                var travelInputs = dayGroup.Select(HistoricalLegacyParityAdapter.ToTravelInput).ToList();
                var rowResults = LegacyTravelDerivation.CalculateRows(travelInputs);
                var dayResult = LegacyTravelDerivation.CalculateDay(resourceId, dayGroup.Key.Value, travelInputs);
                var byId = rowResults.ToDictionary(result => result.PerformanceId);

                foreach (var row in dayGroup)
                {
                    var id = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
                    var calculated = byId[id];
                    if (!NearlyEqual(row.TravelStartHours, dayResult.TravelStartDeductionHours) ||
                        !NearlyEqual(row.TravelEndHours, dayResult.TravelEndDeductionHours) ||
                        !NearlyEqual(row.Extra15Hours, calculated.Extra15Hours))
                    {
                        mismatchCount++;
                    }
                }
            }

            output.WriteLine($"{name}: travel row mismatches={mismatchCount}");
            Assert.Equal(0, mismatchCount);
        }
    }

    [Fact]
    public void PrestatieSortering_NotExported_IdProjPrestIsOnlyDeterministicSortKey()
    {
        if (!TryLoadDetail(out var detail))
        {
            return;
        }

        var numericIds = detail.Count(row =>
            long.TryParse(row.PerformanceId?.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        var syntheticIds = detail.Count - numericIds;
        output.WriteLine(
            $"Prestatie sortering absent; IDPROJ_PREST numeric={numericIds} synthetic={syntheticIds}. SortKey uses stable ID key.");
        Assert.Equal(detail.Count, detail.Select(row => HistoricalLegacyParityAdapter.DefaultSortKey(row)).Distinct().Count());
    }

    private static decimal SumCalculatedOverlap(IEnumerable<PowerBiDetailRow> rows)
    {
        decimal total = 0m;
        foreach (var dayGroup in rows.GroupBy(row => new { row.ResourceId, row.Date }))
        {
            if (dayGroup.Key.Date is null)
            {
                continue;
            }

            var inputs = dayGroup
                .Select(row => HistoricalLegacyParityAdapter.ToOverlapInput(
                    row,
                    HistoricalLegacyParityAdapter.DefaultSortKey(row)))
                .ToList();
            var results = LegacyOverlapCalculator.Calculate(inputs);
            total += results.Sum(result => result.OverlapHours);
        }

        return total;
    }

    private static bool NearlyEqual(decimal? expected, decimal actual)
    {
        if (expected is null)
        {
            return actual == 0m;
        }

        return Math.Abs(expected.Value - actual) <= HourTolerance;
    }

    private bool TryLoadDetail(out IReadOnlyList<PowerBiDetailRow> detail)
    {
        if (!File.Exists(DetailPath))
        {
            output.WriteLine("SKIP: golden master detail CSV not present.");
            detail = [];
            return false;
        }

        detail = PowerBiGoldenMasterReader.ReadDetail(DetailPath);
        return true;
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

using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Local acceptance against private Power BI reference exports.
/// Skips cleanly when reference files are absent (CI / other machines).
/// </summary>
public sealed class July2026LocalReconciliationTests(ITestOutputHelper output)
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

    private static readonly string[] Cohort =
    [
        "Hussain Amiri",
        "Rayn Buyl",
        "Hamza Boudarssissi",
        "Jonas Deklerck",
        "Kevin Van Malderen",
        "Ivo Van Breedam",
    ];

    private const decimal ToleranceMinutes = 1m;

    [Fact]
    public void July2026_Cohort_DailyMaxSum_ReconcilesToOverviewTotalWithinOneMinute()
    {
        if (!TryLoadReference(out var overview, out var detail))
        {
            return;
        }

        Assert.True(overview.Count > 0, $"Overview row count was {overview.Count}.");
        Assert.True(detail.Count > 0, $"Detail row count was {detail.Count}.");

        var failures = new List<string>();
        foreach (var name in Cohort)
        {
            var overviewRow = overview.SingleOrDefault(row =>
                string.Equals(row.Resource, name, StringComparison.Ordinal));
            if (overviewRow is null)
            {
                failures.Add($"{name}: missing from overview.");
                continue;
            }

            if (overviewRow.TotalHours is null)
            {
                failures.Add($"{name}: overview Totaal CJ is blank.");
                continue;
            }

            var dailyMaxSum = PowerBiGoldenMasterReader.SumDailyMaxTotalHours(detail, name);
            var differenceHours = dailyMaxSum - overviewRow.TotalHours.Value;
            var differenceMinutes = differenceHours * 60m;
            var absMinutes = Math.Abs(differenceMinutes);

            output.WriteLine(
                $"{name}: detailDailyMaxSum={dailyMaxSum} overviewTotaalCJ={overviewRow.TotalHours} " +
                $"diffMinutes={differenceMinutes}");

            // <= 1 minute tolerance; tiny float residue at the boundary is accepted.
            if (absMinutes > ToleranceMinutes + 0.001m)
            {
                failures.Add(
                    $"{name}: detailDailyMaxSum={dailyMaxSum} overview={overviewRow.TotalHours} " +
                    $"diffMinutes={differenceMinutes}");
            }
        }

        Assert.True(
            failures.Count == 0,
            string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void July2026_ReferenceFiles_ReportActualCounts_WhenPresent()
    {
        if (!TryLoadReference(out var overview, out var detail))
        {
            return;
        }

        output.WriteLine($"Overview rows={overview.Count}; Detail rows={detail.Count}");

        // Design expected 53 / 1899; assert actual loaded counts are stable and non-zero.
        Assert.Equal(53, overview.Count);
        Assert.Equal(1899, detail.Count);

        foreach (var name in Cohort)
        {
            Assert.Contains(overview, row => row.Resource == name);
            Assert.Contains(detail, row => row.Resource == name);
        }
    }

    private bool TryLoadReference(
        out IReadOnlyList<PowerBiOverviewRow> overview,
        out IReadOnlyList<PowerBiDetailRow> detail)
    {
        if (!File.Exists(OverviewPath) || !File.Exists(DetailPath))
        {
            output.WriteLine(
                "SKIP: local Power BI reference CSVs not present under reference/powerbi/2026-07/. " +
                "Unit suite remains green without private payroll data.");
            overview = [];
            detail = [];
            return false;
        }

        overview = PowerBiGoldenMasterReader.ReadOverview(OverviewPath);
        detail = PowerBiGoldenMasterReader.ReadDetail(DetailPath);
        return true;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // Fall back to common relative paths from test output.
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "reference", "powerbi", "2026-07")))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}

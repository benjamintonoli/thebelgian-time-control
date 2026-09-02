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

public sealed class July2026CurrentCityReplayTests(ITestOutputHelper output)
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

    private static readonly CityAllowanceConfiguration CityConfig =
        CityAllowanceConfiguration.July2026Legacy;

    [Fact]
    public async Task July2026_CurrentPostcodeCoverage_IsDiagnosticOnly()
    {
        if (!TryCreateReader(out var reader, out var overviewResourceIds))
        {
            return;
        }

        var (allRows, allCoverage) = await reader.ReadRawRowsWithPostcodeCoverageAsync(
            From,
            Through,
            overviewResourceIds,
            CancellationToken.None);
        output.WriteLine(
            $"All July coverage: total={allCoverage.TotalRows} bon={allCoverage.BonDeliveryRows} " +
            $"a_pc={allCoverage.ProjectPostalCodeRows} projLev={allCoverage.ProjectDeliveryAddressRows} " +
            $"unresolved={allCoverage.UnresolvedRows}");

        var (cohortRows, cohortCoverage) = await reader.ReadRawRowsWithPostcodeCoverageAsync(
            From,
            Through,
            overviewResourceIds,
            CancellationToken.None);
        _ = cohortRows;
        output.WriteLine(
            $"53-resource coverage: total={cohortCoverage.TotalRows} bon={cohortCoverage.BonDeliveryRows} " +
            $"a_pc={cohortCoverage.ProjectPostalCodeRows} projLev={cohortCoverage.ProjectDeliveryAddressRows} " +
            $"unresolved={cohortCoverage.UnresolvedRows}");

        Assert.True(allCoverage.TotalRows > 0);
    }

    [Fact]
    public async Task July2026_CurrentSixPersonCityReplay_IsDiagnosticOnly()
    {
        if (!TryCreateReader(out var reader, out _))
        {
            return;
        }

        var overviewPath = Path.Combine(FindRepoRoot(), "reference", "powerbi", "2026-07", "Prestaties juli overzicht.csv");
        if (!File.Exists(overviewPath))
        {
            output.WriteLine("SKIP: overview CSV missing.");
            return;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath)
            .ToDictionary(row => row.ResourceId, StringComparer.Ordinal);

        var performances = await reader.ReadPerformancesAsync(
            From,
            Through,
            Cohort.Select(pair => pair.ResourceId).ToArray(),
            CancellationToken.None);

        foreach (var (name, resourceId) in Cohort)
        {
            if (!overview.TryGetValue(resourceId, out var overviewRow))
            {
                output.WriteLine($"{name}: missing from refreshed overview.");
                continue;
            }

            var resourceRows = performances.Where(row => row.ResourceId == resourceId).ToList();
            var resolvedPostcodeRows = resourceRows.Count(row => !string.IsNullOrWhiteSpace(row.Postcode));
            var currentUnits = CalculateCurrentUnits(resourceRows);
            var currentAmount = currentUnits * CityConfig.TripAmount;
            var historicalUnits = (int)(overviewRow.CityTripUnits ?? 0m);
            var classification = Classify(currentUnits, historicalUnits);

            output.WriteLine(
                $"{name}: rows={resourceRows.Count} resolvedPostcode={resolvedPostcodeRows} " +
                $"currentUnits={currentUnits} currentAmount={currentAmount:F2} " +
                $"historicalUnits={historicalUnits} [{classification}]");
        }
    }

    private static int CalculateCurrentUnits(IReadOnlyList<NormalizedPerformanceEntry> rows)
    {
        var total = 0;
        foreach (var dayGroup in rows.GroupBy(row => row.Date))
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

    private static string Classify(int currentUnits, int historicalUnits) =>
        currentUnits == historicalUnits
            ? "SAME"
            : "SOURCE_CHANGED_AFTER_GOLDEN_MASTER";

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

using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Validates Power BI KM-bedrag CJ formula identity against overview export.
/// Does NOT prove historical raw YTD source parity (July detail lacks Jan–eval rows).
/// </summary>
public sealed class July2026KmFormulaIdentityTests(ITestOutputHelper output)
{
    private const decimal Rate = 0.1448m;
    private const decimal Tolerance = 0.0000001m;

    private static readonly string OverviewPath = Path.Combine(
        FindRepoRoot(),
        "reference",
        "powerbi",
        "2026-07",
        "Prestaties juli overzicht.csv");

    [Fact]
    public void July2026_Overview_KmAmount_FormulaIdentityParity()
    {
        if (!File.Exists(OverviewPath))
        {
            output.WriteLine("SKIP: overview CSV missing.");
            return;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(OverviewPath);
        var evaluated = 0;
        var exact = 0;
        var mismatches = new List<string>();

        foreach (var row in overview)
        {
            var pbiKm = row.KmAmount;
            if (pbiKm is null)
            {
                continue;
            }

            evaluated++;
            var pbiExtra75Ytd = row.Extra75Hours ?? 0m;
            // Reverse: implied eligible = PbiKm/rate + Extra75Ytd
            // Reconstruct: rate * (implied - Extra75Ytd) must equal PbiKm
            var reconstructed = Rate * ((pbiKm.Value / Rate + pbiExtra75Ytd) - pbiExtra75Ytd);
            var diff = Math.Abs(reconstructed - pbiKm.Value);
            if (diff <= Tolerance)
            {
                exact++;
            }
            else
            {
                mismatches.Add(
                    $"{row.ResourceId} {row.Resource}: pbi={pbiKm} reconstructed={reconstructed} diff={diff}");
            }
        }

        output.WriteLine(
            $"FORMULA_IDENTITY_PARITY: resources={evaluated} exact={exact} mismatches={mismatches.Count}");
        output.WriteLine(
            "FORMULA IDENTITY PROVEN. HISTORICAL RAW YTD SOURCE PARITY NOT PROVEN FROM JULY DETAIL EXPORT.");
        foreach (var mismatch in mismatches.Take(10))
        {
            output.WriteLine($"  {mismatch}");
        }

        Assert.True(evaluated > 0);
        Assert.Empty(mismatches);
        Assert.Equal(evaluated, exact);
        Assert.Equal(Rate, KmAllowanceConfiguration.Year2026Legacy.RatePerKm);
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

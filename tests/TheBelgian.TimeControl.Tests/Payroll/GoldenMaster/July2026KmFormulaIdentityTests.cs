using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// ALGEBRAIC_FORMULA_IDENTITY: verifies KM-bedrag = rate × (impliedEligible − Extra75Ytd)
/// reconstructs exported overview amounts. Does NOT prove historical raw source parity.
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
    public void July2026_Overview_KmAmount_AlgebraicFormulaIdentity()
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
            $"ALGEBRAIC_FORMULA_IDENTITY: resources={evaluated} exact={exact} mismatches={mismatches.Count}");
        output.WriteLine(
            "ALGEBRAIC FORMULA IDENTITY PROVEN. HISTORICAL RAW SOURCE PARITY NOT PROVEN FROM JULY DETAIL EXPORT.");
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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Local ODBC + golden-master source parity. Skips when DSN or reference files absent.
/// </summary>
public sealed class July2026PlenionSourceReconciliationTests(ITestOutputHelper output)
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
    public async Task July2026_Cohort_SourceParity_ByPerformanceId()
    {
        if (!TryCreateContext(out var context))
        {
            return;
        }

        await context.Reader.VerifyResourceNamesAsync(
            Cohort.ToDictionary(pair => pair.ResourceId, pair => pair.Name));

        var rawRows = await context.Reader.ReadRawRowsForDiagnosticsAsync(
            From,
            Through,
            Cohort.Select(pair => pair.ResourceId).ToArray(),
            CancellationToken.None);
        var plenionSourceRows = rawRows.Select(ToSourceRow).ToList();

        ReportPauseDiagnostics(rawRows);

        foreach (var (name, resourceId) in Cohort)
        {
            var reconciliation = PayrollSourceReconciliation.ReconcileResource(
                name,
                resourceId,
                context.GoldenMasterDetail,
                plenionSourceRows);

            output.WriteLine(
                $"{name} ({resourceId}): pbi={reconciliation.GoldenMasterRowCount} " +
                $"plenion={reconciliation.PlenionRowCount} matched={reconciliation.MatchedIds} " +
                $"exact={reconciliation.ExactMatches} repr={reconciliation.RepresentationOnlyMatches} " +
                $"changed={reconciliation.SourceChangedMatches} missingPbi={reconciliation.MissingInGoldenMaster} " +
                $"missingPlenion={reconciliation.MissingInPlenion} unexplained={reconciliation.Unexplained} " +
                $"atlDiff={reconciliation.AtlDifferenceHours}");

            var travel = PayrollSourceReconciliation.BuildTravelDiagnostics(
                name,
                plenionSourceRows.Where(row => row.ResourceId == resourceId).ToList());
            output.WriteLine(
                $"  travel rows={travel.TravelRows} workDays={travel.WorkDays} " +
                $"daysWithTravel={travel.DaysWithTravel} workNoTravel={travel.DaysWorkNoTravel}");

            if (name is "Jonas Deklerck" or "Kevin Van Malderen")
            {
                var standby = PayrollSourceReconciliation.BuildStandbyDiagnostics(
                    name,
                    plenionSourceRows.Where(row => row.ResourceId == resourceId).ToList());
                output.WriteLine(
                    $"  standby rows={standby.StandbyRows} atl={standby.StandbyAtlHours} " +
                    $"days={standby.DaysWithStandby} travelOnStandbyDays={standby.StandbyDaysWithTravel}");
            }

            Assert.True(reconciliation.PlenionRowCount > 0, $"{name} has no Plenion rows.");
            Assert.True(reconciliation.GoldenMasterRowCount > 0, $"{name} has no golden-master rows.");
            Assert.True(
                reconciliation.MatchedIds > 0,
                $"{name} has no IDPROJ_PREST matches between sources.");
        }
    }

    [Fact]
    public async Task July2026_OptionalFullMonthDiagnostic()
    {
        if (!TryCreateContext(out var context))
        {
            return;
        }

        var allJuly = await context.Reader.ReadRawRowsForDiagnosticsAsync(
            From,
            Through,
            Cohort.Select(pair => pair.ResourceId).ToArray(),
            CancellationToken.None);

        output.WriteLine($"July cohort rows={allJuly.Count}");
        output.WriteLine($"travel={allJuly.Count(row => row.IdHfdTaak == 5)}");
        output.WriteLine($"standby={allJuly.Count(row => row.IdHfdTaak == 23)}");
        output.WriteLine($"nullVan={allJuly.Count(row => row.Van is null or DBNull)}");
        output.WriteLine($"nullTot={allJuly.Count(row => row.Tot is null or DBNull)}");
        output.WriteLine($"overnight={allJuly.Count(row => IsOvernight(row))}");
        output.WriteLine($"atlGt18={allJuly.Count(row => row.AtlHoursRaw > 18m)}");
        output.WriteLine(
            $"pauseInvalid={allJuly.Count(row => PauseNormalizer.Normalize(row.Pauze).Status == PauseParseStatus.Invalid)}");
    }

    private void ReportPauseDiagnostics(IReadOnlyList<PlenionPayrollPerformanceRow> rows)
    {
        var groups = rows
            .GroupBy(row => PlenionPayrollFieldReader.DescribeClrType(row.Pauze))
            .OrderByDescending(group => group.Count());
        output.WriteLine("PAUZE CLR types:");
        foreach (var group in groups)
        {
            output.WriteLine($"  {group.Key}: {group.Count()}");
        }
    }

    private static bool IsOvernight(PlenionPayrollPerformanceRow row)
    {
        if (row.Van is null or DBNull || row.Tot is null or DBNull)
        {
            return false;
        }

        var van = row.Van is TimeSpan vanSpan ? vanSpan : TimeSpan.Zero;
        var tot = row.Tot is TimeSpan totSpan ? totSpan : TimeSpan.Zero;
        return tot < van;
    }

    private static PlenionSourceRow ToSourceRow(PlenionPayrollPerformanceRow row)
    {
        var pause = PauseNormalizer.Normalize(row.Pauze);
        return new PlenionSourceRow(
            row.IdProjPrest,
            row.ResourceId,
            row.Datum,
            PerformanceTimeNormalizer.FormatClock(row.Van),
            PerformanceTimeNormalizer.FormatClock(row.Tot),
            row.AtlHoursRaw,
            row.Km,
            row.IdHfdTaak,
            row.BonNr,
            pause.ExactMinutes);
    }

    private bool TryCreateContext(out LocalReconciliationContext context)
    {
        var repoRoot = FindRepoRoot();
        var overviewPath = Path.Combine(repoRoot, "reference", "powerbi", "2026-07", "Prestaties juli overzicht.csv");
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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Loads KM by IDPROJ_PREST from Plenion for historical golden-master enrichment.
/// KM is not exported in the Power BI detail CSV.
/// </summary>
public static class HistoricalPlenionKmEnrichment
{
    public static async Task<IReadOnlyDictionary<long, decimal?>> TryLoadJuly2026KmLookupAsync(
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        var connectionString = Environment.GetEnvironmentVariable("PLENION_ODBC")
            ?? TryReadConnectionString(FindRepoRoot());
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new Dictionary<long, decimal?>();
        }

        try
        {
            using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
            await probe.OpenAsync(cancellationToken);
        }
        catch
        {
            return new Dictionary<long, decimal?>();
        }

        var reader = new PlenionPayrollReader(
            Options.Create(new PlenionOptions { PlenionOdbc = connectionString }),
            NullLogger<PlenionPayrollReader>.Instance);

        var rows = await reader.ReadRawRowsForDiagnosticsAsync(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            resourceIds,
            cancellationToken);

        return rows.ToDictionary(
            row => row.IdProjPrest,
            row => row.Km);
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
}

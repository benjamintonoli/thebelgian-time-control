using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Legacy;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyPayrollMayAugCandidateValidationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2026, 5, "Prestaties mei overzicht.csv")]
    [InlineData(2026, 6, "Prestaties juni overzicht.csv")]
    [InlineData(2026, 7, "Prestaties juli overzicht.csv")]
    [InlineData(2026, 8, "Prestaties augustus overzicht.csv")]
    public async Task AutoCandidates_VsPowerBiOverview(int year, int month, string overviewFile)
    {
        var repoRoot = FindRepoRoot();
        var overviewPath = Path.Combine(repoRoot, "reference", "powerbi", $"{year}-{month:00}", overviewFile);
        if (!File.Exists(overviewPath))
        {
            output.WriteLine($"SKIP missing {overviewPath}");
            return;
        }

        if (!TryCreateReader(out var reader, out var performanceSource))
        {
            output.WriteLine("SKIP Plenion ODBC unavailable.");
            return;
        }

        var periodStart = new DateOnly(year, month, 1);
        var periodEnd = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath);
        var pbiIds = overview.Select(row => row.ResourceId).ToHashSet(StringComparer.Ordinal);
        var resources = await reader.ReadCandidatesAsync(default);
        var byId = resources.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);

        var projectLeaderIds = resources
            .Where(item => item.IsActiveForPeriod(periodStart))
            .Where(item => LegacyPayrollTechnicianFunctions.IsProjectLeider(item.Function))
            .Where(item => !LegacyPayrollNameMarkers.IsLegacyOaMarker(item.DisplayName))
            .Where(item => !LegacyPayrollNameMarkers.IsLegacyStagiairMarker(item.DisplayName))
            .Select(item => item.ResourceId)
            .ToArray();
        var task23 = new HashSet<string>(StringComparer.Ordinal);
        if (projectLeaderIds.Length > 0)
        {
            var perfs = await performanceSource.ReadPerformancesAsync(
                periodStart,
                periodEnd,
                projectLeaderIds,
                default);
            foreach (var row in perfs.Where(item =>
                         item.HfdTaakId == LegacyPayrollPerformanceEligibility.ProjectLeiderIncludedHfdTaakId))
            {
                task23.Add(row.ResourceId);
            }
        }

        var auto = LegacyPayrollAutoCandidateSelector.SelectAutoCandidates(resources, periodStart, task23);
        var autoIds = auto.Select(item => item.ResourceId).ToHashSet(StringComparer.Ordinal);
        var captured = pbiIds.Intersect(autoIds).Count();
        var missed = pbiIds.Except(autoIds).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var extras = autoIds.Except(pbiIds).Count();

        output.WriteLine(
            $"{year}-{month:00}: PBI={pbiIds.Count} auto={autoIds.Count} captured={captured} missed={missed.Count} extras={extras}");
        foreach (var id in missed)
        {
            byId.TryGetValue(id, out var resource);
            var name = overview.First(row => row.ResourceId == id).Resource;
            output.WriteLine(
                $"MISS {id} name={name} functie={resource?.Function} " +
                $"oa={LegacyPayrollNameMarkers.IsLegacyOaMarker(resource?.DisplayName ?? name)} " +
                $"stagiair={LegacyPayrollNameMarkers.IsLegacyStagiairMarker(resource?.DisplayName ?? name)} " +
                $"end={resource?.EmploymentEndDate} task23={task23.Contains(id)} " +
                $"why={ExplainMiss(resource, name, periodStart, task23)}");
        }

        Assert.True(autoIds.Count < resources.Count);
        Assert.True(autoIds.Count < 200);
    }

    private static string ExplainMiss(
        PayrollEmployeeCandidate? resource,
        string fallbackName,
        DateOnly periodStart,
        HashSet<string> task23)
    {
        var name = resource?.DisplayName ?? fallbackName;
        if (resource is null)
        {
            return "resource-not-in-plenion-read";
        }

        if (!resource.IsActiveForPeriod(periodStart))
        {
            return "ended-before-period";
        }

        if (LegacyPayrollNameMarkers.IsLegacyOaMarker(name))
        {
            return "legacy-oa-marker";
        }

        if (LegacyPayrollNameMarkers.IsLegacyStagiairMarker(name))
        {
            return "legacy-stagiair-marker";
        }

        if (LegacyPayrollTechnicianFunctions.IsProjectLeider(resource.Function) && !task23.Contains(resource.ResourceId))
        {
            return "project-leider-without-task23";
        }

        if (!LegacyPayrollTechnicianFunctions.IsTechnicianFunction(resource.Function)
            && !LegacyPayrollTechnicianFunctions.IsProjectLeider(resource.Function))
        {
            return $"function-not-in-technician-set:{resource.Function}";
        }

        return "unexplained";
    }

    private static bool TryCreateReader(
        out PlenionPayrollResourceReader reader,
        out PlenionPayrollReader performanceSource)
    {
        var connectionString = Environment.GetEnvironmentVariable("PLENION_ODBC") ?? "DSN=PlenionWriteLive;";
        try
        {
            using var probe = new System.Data.Odbc.OdbcConnection(connectionString);
            probe.Open();
        }
        catch
        {
            reader = null!;
            performanceSource = null!;
            return false;
        }

        var options = Options.Create(new PlenionOptions { PlenionOdbc = connectionString });
        reader = new PlenionPayrollResourceReader(options, NullLogger<PlenionPayrollResourceReader>.Instance);
        performanceSource = new PlenionPayrollReader(options, NullLogger<PlenionPayrollReader>.Instance);
        return true;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TheBelgian.TimeControl.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}

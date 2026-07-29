using System.Text;
using Microsoft.AspNetCore.DataProtection;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Web.Pages.Pilot;

var isBroader = args.Contains("--broader-validation", StringComparer.OrdinalIgnoreCase);
var isCoverageGap = args.Contains("--coverage-gap", StringComparer.OrdinalIgnoreCase);
var isActivity = args.Contains("--activity-classification", StringComparer.OrdinalIgnoreCase);
var isAdaptive = args.Contains("--adaptive-location-matching", StringComparer.OrdinalIgnoreCase);

if (isBroader || isCoverageGap || isActivity || isAdaptive)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    builder.Services.AddTimeControlInfrastructure(builder.Configuration);
    await using var host = builder.Build();
    await host.Services.InitializeTimeControlDatabaseAsync();
    await using var scope = host.Services.CreateAsyncScope();
    var service = scope.ServiceProvider.GetRequiredService<IBroaderValidationPilotService>();
    var from = ParseDate(args, "--from", new DateOnly(2026, 7, 1));
    var through = ParseDate(args, "--through", new DateOnly(2026, 7, 28));
    var broader = await service.RunAsync(
        new BroaderValidationRequest(
            BroaderModel.DefaultTechnicianNames
                .Select(name => new BroaderValidationTechnicianRequest(name))
                .ToArray(),
            from,
            through,
            5),
        CancellationToken.None);
    var docsPath = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, "..", "..", "docs"));
    Directory.CreateDirectory(docsPath);
    var cachePath = BroaderValidationCache.DefaultPath(docsPath);
    if (broader.Summary.ProcessedTechnicianCount > 0)
    {
        BroaderValidationCache.Save(cachePath, broader);
    }
    else if (isAdaptive)
    {
        var cached = BroaderValidationCache.TryLoad(cachePath);
        if (cached is null || cached.Summary.ProcessedTechnicianCount <= 0)
        {
            Console.Error.WriteLine(
                "Adaptive matching afgebroken: Plenion onbereikbaar en geen lokale full-cache.");
            Console.WriteLine("BaselineCoverage=39");
            Console.WriteLine("AdaptiveWithoutLearning=n/a");
            Console.WriteLine("AdaptiveWithLearning=n/a");
            Console.WriteLine("PrecisionKind=estimated");
            Console.WriteLine("PrecisionPercent=n/a");
            Console.WriteLine("LearnedClusters=n/a");
            Console.WriteLine("Ambiguous=n/a");
            Console.WriteLine("Unresolved=n/a");
            Console.WriteLine(
                "LargestGain=Historische clusters 251-500m + overlappercentage (prior gap: +15 cases / ~57% plafond)");
            Console.WriteLine("TargetEightyResponsible=False");
            Console.WriteLine(
                "NextStep=Herstel DNS/VPN naar tbws01:4900 en herhaal --adaptive-location-matching op dezelfde 82-noemer.");
            return;
        }

        broader = cached;
        Console.WriteLine($"UsingCache={cachePath}");
    }

    if (isAdaptive)
    {
        var analysis = AdaptiveLocationValidationService.Analyze(broader, docsPath);
        var markdownPath = Path.Combine(docsPath, "adaptive-location-matching-report.md");
        var jsonPath = Path.Combine(docsPath, "adaptive-location-matching-report.json");
        await File.WriteAllTextAsync(
            markdownPath,
            AdaptiveLocationReportWriter.ToMarkdown(analysis),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            AdaptiveLocationReportWriter.ToJson(analysis),
            Encoding.UTF8);
        Console.WriteLine($"BaselineCoverage={analysis.Baseline.ReliableCoveragePercent}");
        Console.WriteLine(
            $"AdaptiveWithoutLearning={analysis.AdaptiveWithoutLearning.ReliableCoveragePercent}");
        Console.WriteLine(
            $"AdaptiveWithLearning={analysis.AdaptiveWithLearning.ReliableCoveragePercent}");
        Console.WriteLine($"PrecisionKind={analysis.PrecisionKind}");
        Console.WriteLine($"PrecisionPercent={analysis.PrecisionPercent}");
        Console.WriteLine($"LearnedClusters={analysis.LearnedClusterCount}");
        Console.WriteLine($"Ambiguous={analysis.AdaptiveWithLearning.Ambiguous}");
        Console.WriteLine($"Unresolved={analysis.AdaptiveWithLearning.Unresolved}");
        Console.WriteLine($"LargestGain={analysis.LargestGainRules}");
        Console.WriteLine(
            $"TargetEightyResponsible={analysis.TargetEightyPercentResponsible}");
        Console.WriteLine($"SelectedConfig={analysis.SelectedConfiguration.Name}");
        Console.WriteLine($"NextStep={analysis.RecommendedNextStep}");
        Console.WriteLine($"Sample={analysis.StratifiedSamplePath}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    if (isActivity)
    {
        var analysis = ActivityClassificationAnalysisService.Analyze(broader);
        var markdownPath = Path.Combine(docsPath, "activity-classification-report.md");
        var jsonPath = Path.Combine(docsPath, "activity-classification-report.json");
        await File.WriteAllTextAsync(
            markdownPath,
            ActivityClassificationReportWriter.ToMarkdown(analysis),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            ActivityClassificationReportWriter.ToJson(analysis),
            Encoding.UTF8);
        foreach (var summary in analysis.TypeSummaries)
        {
            Console.WriteLine(
                $"Type={summary.ActivityType}|Count={summary.PerformanceCount}|GeoRequired={summary.RequiresGeographicMatchCount}|IncorrectDenominator={summary.IncorrectlyInLocationDenominatorCount}|Unknown={summary.UnknownCount}|HFDTAAK={string.Join(';', summary.MainTaskCodes)}");
        }

        Console.WriteLine($"OpenCases={analysis.OpenCases.OpenCaseCount}");
        Console.WriteLine($"OpenNotLocationBound={analysis.OpenCases.NotLocationBoundCount}");
        Console.WriteLine($"OpenStillLocationBound={analysis.OpenCases.StillLocationBoundCount}");
        Console.WriteLine($"OpenUnknown={analysis.OpenCases.UnknownCount}");
        Console.WriteLine(
            $"CorrectedReliablePercent={analysis.CorrectedMatch.CorrectedReliablePercent}");
        Console.WriteLine(
            $"LocationBoundResolutions={analysis.CorrectedMatch.LocationBoundResolutionCount}");
        Console.WriteLine(
            $"RemainingNoReliableMatch={analysis.CorrectedMatch.RemainingNoReliableMatchCount}");
        Console.WriteLine(
            $"AliasFlippableLocationBound={analysis.CorrectedMatch.AliasFlippableLocationBoundCount}");
        Console.WriteLine(
            $"PotentialAfterAlias={analysis.CorrectedMatch.PotentialReliablePercentAfterAliases}");
        Console.WriteLine($"Advice={analysis.AliasAdvice}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    if (isCoverageGap)
    {
        var analysis = CoverageGapAnalysisService.Analyze(broader);
        var markdownPath = Path.Combine(docsPath, "coverage-gap-analysis.md");
        var jsonPath = Path.Combine(docsPath, "coverage-gap-analysis.json");
        await File.WriteAllTextAsync(
            markdownPath,
            CoverageGapReportWriter.ToMarkdown(analysis),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            CoverageGapReportWriter.ToJson(analysis),
            Encoding.UTF8);
        Console.WriteLine($"EmployeeLinks={analysis.EmployeeLinks.Count}");
        foreach (var link in analysis.EmployeeLinks)
        {
            Console.WriteLine(
                $"Link={link.PlenionOmschr}|IDRESOURCE={link.PlenionIdResource}|RESCODE={link.PlenionResCode}|driverid={link.PowerfleetDriverId}|drivername={link.PowerfleetDriverName}|objects={string.Join(',', link.InformativeObjectNames)}|key=driverid");
        }

        Console.WriteLine($"ReliablePercent={analysis.MatchBreakdown.ReliablePercent}");
        Console.WriteLine($"UnreliablePercent={analysis.MatchBreakdown.UnreliablePercent}");
        Console.WriteLine($"Cause={analysis.MatchBreakdown.PrimaryCause}");
        Console.WriteLine(
            $"UniqueProblemLocations={analysis.AliasProjection.UniqueProblemLocations}");
        Console.WriteLine(
            $"ConfirmableAliases={analysis.AliasProjection.UniqueConfirmableAliases}");
        Console.WriteLine(
            $"FlippablePerformances={analysis.AliasProjection.PerformancesFlippedIfAllAliasesConfirmed}");
        Console.WriteLine(
            $"PotentialReliablePercent={analysis.AliasProjection.PotentialReliablePercentAfterAliasConfirmation}");
        Console.WriteLine($"Top20Gain={analysis.AliasProjection.Top20GainPerformances}");
        Console.WriteLine($"Advice={analysis.AliasTableAdvice}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    var broaderMarkdown = Path.Combine(docsPath, "broader-validation-report.md");
    var broaderJson = Path.Combine(docsPath, "broader-validation-report.json");
    await File.WriteAllTextAsync(
        broaderMarkdown,
        BroaderValidationReportWriter.ToMarkdown(broader),
        Encoding.UTF8);
    await File.WriteAllTextAsync(
        broaderJson,
        BroaderValidationReportWriter.ToJson(broader),
        Encoding.UTF8);
    Console.WriteLine($"ProcessedTechnicians={broader.Summary.ProcessedTechnicianCount}");
    Console.WriteLine($"Workdays={broader.Summary.WorkdayCount}");
    Console.WriteLine($"Performances={broader.Summary.TotalPerformanceCount}");
    Console.WriteLine($"ReliableMatchPercent={broader.Summary.ReliableMatchPercent}");
    Console.WriteLine($"ConfirmedPercent={broader.Summary.ConfirmedPercent}");
    Console.WriteLine($"ProbablePercent={broader.Summary.ProbablePercent}");
    Console.WriteLine($"ManualReviewPercent={broader.Summary.ManualReviewPercent}");
    Console.WriteLine($"MissingDriverTrips={broader.Summary.MissingDriverTripCount}");
    Console.WriteLine($"PossibleHourDeviations={broader.Summary.PossibleHourDeviationCount}");
    Console.WriteLine($"IndividualDeviations={broader.Summary.IndividualToleranceDeviationCount}");
    Console.WriteLine($"HighPriorityDeviations={broader.Summary.HighPriorityToleranceDeviationCount}");
    Console.WriteLine($"Report={broaderMarkdown}");
    foreach (var skipped in broader.Summary.SkippedTechnicians)
    {
        Console.WriteLine($"Skipped={skipped}");
    }

    foreach (var problem in broader.Summary.RecurringAddressProblems)
    {
        Console.WriteLine($"AddressProblem={problem}");
    }

    return;
}

var webBuilder = WebApplication.CreateBuilder(args);

webBuilder.Logging.ClearProviders();
webBuilder.Logging.AddConsole();
webBuilder.Logging.AddDebug();
webBuilder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var dataProtectionDirectory = Path.Combine(
    webBuilder.Environment.ContentRootPath,
    "data",
    "data-protection-keys");
Directory.CreateDirectory(dataProtectionDirectory);

webBuilder.Services.AddRazorPages();
var dataProtection = webBuilder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory))
    .SetApplicationName("TheBelgian.TimeControl");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

webBuilder.Services.AddTimeControlInfrastructure(webBuilder.Configuration);

var app = webBuilder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
await app.Services.InitializeTimeControlDatabaseAsync();

app.Run();

static DateOnly ParseDate(string[] arguments, string name, DateOnly fallback)
{
    var index = Array.FindIndex(
        arguments,
        item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= arguments.Length)
    {
        return fallback;
    }

    return DateOnly.TryParse(arguments[index + 1], out var parsed)
        ? parsed
        : fallback;
}

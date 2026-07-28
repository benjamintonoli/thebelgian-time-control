using System.Text;
using Microsoft.AspNetCore.DataProtection;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Web.Pages.Pilot;

if (args.Contains("--broader-validation", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--coverage-gap", StringComparer.OrdinalIgnoreCase))
{
    var runCoverageGap = args.Contains("--coverage-gap", StringComparer.OrdinalIgnoreCase);
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

    if (runCoverageGap)
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

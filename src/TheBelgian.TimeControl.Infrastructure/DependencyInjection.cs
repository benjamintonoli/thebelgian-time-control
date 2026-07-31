using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Geocoding;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Infrastructure.Plenion;
using TheBelgian.TimeControl.Infrastructure.Powerfleet;
using TheBelgian.TimeControl.Infrastructure.Synchronization;

namespace TheBelgian.TimeControl.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTimeControlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MatchingOptions>()
            .Bind(configuration.GetSection(MatchingOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "Matchingtoleranties moeten oplopend zijn.")
            .ValidateOnStart();
        services.Configure<PlenionOptions>(
            configuration.GetSection(PlenionOptions.SectionName));
        services.AddOptions<PowerfleetOptions>()
            .Bind(configuration.GetSection(PowerfleetOptions.SectionName))
            .Validate(
                options => string.IsNullOrWhiteSpace(options.BaseUrl) ||
                           Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
                "Powerfleet:BaseUrl moet leeg of een absolute URL zijn.");
        services.AddOptions<GeocodingOptions>()
            .Bind(configuration.GetSection(GeocodingOptions.SectionName));
        services.AddOptions<LocationMatchingOptions>()
            .Bind(configuration.GetSection(LocationMatchingOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "Locatieafstandsgrenzen moeten positief en oplopend zijn.")
            .ValidateOnStart();
        services.AddOptions<AdaptiveLocationMatchingOptions>()
            .Bind(configuration.GetSection(AdaptiveLocationMatchingOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "Adaptieve locatieparameters moeten geldig zijn.")
            .ValidateOnStart();
        var sqliteConnection = configuration.GetConnectionString("TimeControl")
            ?? "Data Source=data/time-control.db";
        services.AddDbContextFactory<TimeControlDbContext>(options =>
            options.UseSqlite(sqliteConnection));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<MatchingOptions>>().Value);
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<LocationMatchingOptions>>().Value);
        services.AddSingleton<IDistanceCalculator, HaversineDistanceCalculator>();
        services.AddScoped<ITimeControlMatchingService, TimeControlMatchingService>();
        services.AddScoped<IPlenionReader, OdbcPlenionReader>();
        services.AddSingleton<PowerfleetXmlParser>();
        services.AddHttpClient<IPowerfleetClient, PowerfleetClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddScoped<IExceptionRepository, ExceptionRepository>();
        services.AddScoped<ISourceDataRepository, SourceDataRepository>();
        services.AddScoped<ISynchronizationService, SynchronizationService>();
        services.AddScoped<PilotPlenionReader>();
        services.AddHttpClient<AzureMapsGeocodingService>(
            client =>
            {
                client.BaseAddress = new Uri("https://atlas.microsoft.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddHttpClient<GeoapifyGeocodingService>(
            client =>
            {
                client.BaseAddress = new Uri("https://api.geoapify.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddScoped<IGeocodingService>(provider =>
        {
            var providerName = provider
                .GetRequiredService<IOptions<GeocodingOptions>>()
                .Value
                .Provider;
            return providerName.Equals(
                "Geoapify",
                StringComparison.OrdinalIgnoreCase)
                ? provider.GetRequiredService<GeoapifyGeocodingService>()
                : provider.GetRequiredService<AzureMapsGeocodingService>();
        });
        services.AddScoped<LocationGeocodingCache>();
        services.AddScoped<LocationResolutionPilotService>();
        services.AddHttpClient<PilotPowerfleetReader>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddScoped<IReadOnlyPilotService, ReadOnlyPilotService>();
        services.AddScoped<IBroaderValidationPilotService, BroaderValidationPilotService>();
        services.AddScoped<LocationMatchingBenchmarkService>();
        services.AddScoped<CalibrationSingleReviewerEvaluationService>();
        services.AddScoped<LocationMatchingRecoveryAuditService>();
        services.AddScoped<AdminReviewDecisionRepository>();
        services.AddScoped<IAdminReviewService, AdminReviewService>();
        return services;
    }

    public static async Task InitializeTimeControlDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<TimeControlDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var databasePath = context.Database.GetDbConnection().DataSource;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "LocationResolutionCacheEntries" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_LocationResolutionCacheEntries" PRIMARY KEY AUTOINCREMENT,
                "DeliveryAddressExternalId" TEXT NULL,
                "OriginalAddress" TEXT NOT NULL,
                "NormalizedAddress" TEXT NOT NULL,
                "AddressHash" TEXT NOT NULL,
                "Latitude" REAL NULL,
                "Longitude" REAL NULL,
                "ResolvedAddress" TEXT NULL,
                "Confidence" TEXT NULL,
                "Provider" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "ErrorMessage" TEXT NULL,
                "AlternativesJson" TEXT NULL,
                "LastAttemptAt" TEXT NULL,
                "LastSuccessfulResolutionAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocationResolutionCacheEntries_AddressHash"
                ON "LocationResolutionCacheEntries" ("AddressHash");
            CREATE TABLE IF NOT EXISTS "AdminReviewDecisionAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AdminReviewDecisionAudits" PRIMARY KEY AUTOINCREMENT,
                "PerformanceId" INTEGER NOT NULL,
                "OriginalMatcherDecision" TEXT NOT NULL,
                "ProposedVisitCandidateId" TEXT NULL,
                "ProposedVisitSourceStopIdsJson" TEXT NULL,
                "AdminDecision" TEXT NOT NULL,
                "ChosenVisitCandidateId" TEXT NULL,
                "ChosenVisitSourceStopIdsJson" TEXT NULL,
                "Comment" TEXT NULL,
                "Reviewer" TEXT NOT NULL,
                "DecidedAt" TEXT NOT NULL,
                "MatcherCommit" TEXT NOT NULL,
                "ConfigurationHashSha256" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AdminReviewDecisionAudits_PerformanceId_DecidedAt"
                ON "AdminReviewDecisionAudits" ("PerformanceId", "DecidedAt");
            """,
            cancellationToken);
    }
}

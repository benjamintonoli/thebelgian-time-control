using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Persistence;
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

        var sqliteConnection = configuration.GetConnectionString("TimeControl")
            ?? "Data Source=data/time-control.db";
        services.AddDbContextFactory<TimeControlDbContext>(options =>
            options.UseSqlite(sqliteConnection));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<MatchingOptions>>().Value);
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
    }
}

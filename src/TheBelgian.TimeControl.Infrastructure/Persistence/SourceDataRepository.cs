using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Persistence;

public sealed class SourceDataRepository(
    IDbContextFactory<TimeControlDbContext> contextFactory) : ISourceDataRepository
{
    public Task UpsertTechniciansAsync(
        IEnumerable<Technician> technicians,
        CancellationToken cancellationToken) =>
        UpsertTechniciansCoreAsync(technicians.ToArray(), cancellationToken);

    public Task UpsertPerformancesAsync(
        IEnumerable<PlenionPerformance> performances,
        CancellationToken cancellationToken) =>
        UpsertPerformancesCoreAsync(performances.ToArray(), cancellationToken);

    public Task UpsertTripsAsync(
        IEnumerable<PowerfleetTrip> trips,
        CancellationToken cancellationToken) =>
        UpsertTripsCoreAsync(trips.ToArray(), cancellationToken);

    public async Task AddSynchronizationRunAsync(
        SynchronizationRun run,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.SynchronizationRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertTechniciansCoreAsync(
        Technician[] items,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var keys = items.Select(item => item.ExternalId).ToArray();
        var existing = await context.Technicians
            .Where(item => keys.Contains(item.ExternalId))
            .ToDictionaryAsync(item => item.ExternalId, cancellationToken);
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.ExternalId, out var current))
            {
                context.Entry(current).CurrentValues.SetValues(item);
            }
            else
            {
                context.Technicians.Add(item);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertPerformancesCoreAsync(
        PlenionPerformance[] items,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var keys = items.Select(item => item.ExternalId).ToArray();
        var existing = await context.PlenionPerformances
            .Where(item => keys.Contains(item.ExternalId))
            .ToDictionaryAsync(item => item.ExternalId, cancellationToken);
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.ExternalId, out var current))
            {
                context.Entry(current).CurrentValues.SetValues(item);
            }
            else
            {
                context.PlenionPerformances.Add(item);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertTripsCoreAsync(
        PowerfleetTrip[] items,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var keys = items.Select(item => item.ExternalId).ToArray();
        var existing = await context.PowerfleetTrips
            .Where(item => keys.Contains(item.ExternalId))
            .ToDictionaryAsync(item => item.ExternalId, cancellationToken);
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.ExternalId, out var current))
            {
                context.Entry(current).CurrentValues.SetValues(item);
            }
            else
            {
                context.PowerfleetTrips.Add(item);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

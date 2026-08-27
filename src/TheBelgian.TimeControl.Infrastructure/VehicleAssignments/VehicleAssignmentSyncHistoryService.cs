using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

public sealed class VehicleAssignmentSyncHistoryService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    TimeProvider timeProvider)
{
    internal async Task<VehicleAssignmentSyncRun> StartAsync(
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = new VehicleAssignmentSyncRun
        {
            StartedAt = startedAt,
            Status = "Running",
        };
        context.VehicleAssignmentSyncRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
        return run;
    }

    internal async Task CompleteAsync(
        int runId,
        VehicleAssignmentSyncResult result,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.VehicleAssignmentSyncRuns.SingleAsync(
            item => item.Id == runId, cancellationToken);
        run.FinishedAt = finishedAt;
        run.Status = "Succeeded";
        run.DurationSeconds = Math.Max(0, (finishedAt - run.StartedAt).TotalSeconds);
        run.VehiclesRead = result.Vehicles;
        run.PhysicalVehiclesObserved = result.PhysicalVehiclesObserved;
        run.ExactMapped = result.ExactMapped;
        run.AssignmentsOpened = result.AssignmentsOpened;
        run.AssignmentsObserved = result.AssignmentsObserved;
        run.AssignmentsClosed = result.AssignmentsClosed;
        run.Ambiguous = result.Ambiguous;
        run.Unmapped = result.Unmapped;
        run.ResourcesWithoutPersonalVehicle = result.ResourcesWithoutPersonalVehicle;
        run.SkippedNoTrackAndTrace = result.SkippedNoTrackAndTrace;
        await context.SaveChangesAsync(cancellationToken);
    }

    internal async Task FailAsync(
        int runId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var finishedAt = timeProvider.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.VehicleAssignmentSyncRuns.SingleAsync(
            item => item.Id == runId, cancellationToken);
        run.FinishedAt = finishedAt;
        run.Status = "Failed";
        run.DurationSeconds = Math.Max(0, (finishedAt - run.StartedAt).TotalSeconds);
        run.ErrorSummary = CompactError(exception);
        await context.SaveChangesAsync(cancellationToken);
    }

    internal async Task RecordSkippedAlreadyRunningAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.VehicleAssignmentSyncRuns.Add(new VehicleAssignmentSyncRun
        {
            StartedAt = now,
            FinishedAt = now,
            Status = "SkippedAlreadyRunning",
            DurationSeconds = 0,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> LastSuccessfulVehicleAssignmentSyncAtAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var values = await context.VehicleAssignmentSyncRuns.AsNoTracking()
            .Where(item => item.Status == "Succeeded" && item.FinishedAt != null)
            .Select(item => item.FinishedAt)
            .ToArrayAsync(cancellationToken);
        return values.Length == 0 ? null : values.Max();
    }

    private static string CompactError(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= 1000 ? value : value[..1000];
    }
}

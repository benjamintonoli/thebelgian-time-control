using Microsoft.Extensions.Logging;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Infrastructure.Synchronization;

public sealed class SynchronizationService(
    IPlenionReader plenionReader,
    IPowerfleetClient powerfleetClient,
    ITimeControlMatchingService matchingService,
    IExceptionRepository exceptionRepository,
    ISourceDataRepository sourceRepository,
    TimeProvider timeProvider,
    ILogger<SynchronizationService> logger) : ISynchronizationService
{
    public async Task<SynchronizationResult> SynchronizeAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken)
    {
        if (throughDate < fromDate)
        {
            throw new ArgumentException("Einddatum ligt vóór begindatum.", nameof(throughDate));
        }

        var run = new SynchronizationRun
        {
            StartedAt = timeProvider.GetUtcNow(),
            FromDate = fromDate,
            ThroughDate = throughDate,
        };

        try
        {
            var technicians = await plenionReader.GetTechniciansAsync(cancellationToken);
            var performances = await plenionReader.GetPerformancesAsync(
                fromDate,
                throughDate,
                cancellationToken);
            var trips = await powerfleetClient.GetTripsAsync(
                ToBrusselsOffset(fromDate, TimeOnly.MinValue),
                ToBrusselsOffset(throughDate.AddDays(1), TimeOnly.MinValue),
                cancellationToken);

            await sourceRepository.UpsertTechniciansAsync(technicians, cancellationToken);
            await sourceRepository.UpsertPerformancesAsync(performances, cancellationToken);
            await sourceRepository.UpsertTripsAsync(trips, cancellationToken);

            var history = await exceptionRepository.SearchAsync(new ExceptionFilter(), cancellationToken);
            var detected = new List<DetectedException>();
            foreach (var technician in technicians)
            {
                foreach (var date in Dates(fromDate, throughDate))
                {
                    var hasPerformances = performances.Any(item =>
                        item.TechnicianExternalId == technician.ExternalId && item.Date == date);
                    if (!hasPerformances)
                    {
                        continue;
                    }

                    var technicianTrips = trips.Where(item =>
                        string.Equals(item.DriverId, technician.ExternalId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.DriverName, technician.Name, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var vehicleCount = technicianTrips
                        .Where(item => DateOnly.FromDateTime(item.Start.LocalDateTime) == date)
                        .Select(item => item.ObjectId ?? item.VehiclePlate)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                    var timeline = DailyTimelineFactory.Create(
                        technician,
                        date,
                        performances,
                        trips,
                        vehicleCount <= 1);
                    detected.AddRange(matchingService.Detect(timeline, history));
                }
            }

            await exceptionRepository.UpsertAsync(detected, cancellationToken);
            run.CompletedAt = timeProvider.GetUtcNow();
            run.ImportedPlenionCount = performances.Count;
            run.ImportedPowerfleetCount = trips.Count;
            run.DetectedExceptionCount = detected.Count(item => item.Type != ExceptionType.None);
            run.Status = "Completed";
            await sourceRepository.AddSynchronizationRunAsync(run, cancellationToken);

            logger.LogInformation(
                "Lokale synchronisatie afgerond: {PerformanceCount} prestaties, {TripCount} ritten, {ExceptionCount} afwijkingen.",
                performances.Count,
                trips.Count,
                run.DetectedExceptionCount);
            return new SynchronizationResult(
                performances.Count,
                trips.Count,
                run.DetectedExceptionCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.CompletedAt = timeProvider.GetUtcNow();
            run.Status = "Failed";
            run.ErrorMessage = exception.Message;
            await sourceRepository.AddSynchronizationRunAsync(run, cancellationToken);
            logger.LogError(exception, "Lokale synchronisatie is mislukt.");
            throw;
        }
    }

    private static IEnumerable<DateOnly> Dates(DateOnly from, DateOnly through)
    {
        for (var current = from; current <= through; current = current.AddDays(1))
        {
            yield return current;
        }
    }

    private static DateTimeOffset ToBrusselsOffset(DateOnly date, TimeOnly time)
    {
        var value = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }
}

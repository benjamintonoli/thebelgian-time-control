using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPlenionReader
{
    Task<IReadOnlyList<Technician>> GetTechniciansAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PlenionPerformance>> GetPerformancesAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerLocation>> GetCustomerLocationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PlenionWorkOrder>> GetWorkOrdersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PlenionProject>> GetProjectsAsync(CancellationToken cancellationToken);
}

public interface IPowerfleetClient
{
    Task<IReadOnlyList<PowerfleetTrip>> GetTripsAsync(
        DateTimeOffset from,
        DateTimeOffset through,
        CancellationToken cancellationToken);
}

public interface ITimeControlMatchingService
{
    IReadOnlyList<DetectedException> Detect(
        DailyTechnicianTimeline timeline,
        IReadOnlyCollection<DetectedException>? history = null);
}

public interface IExceptionRepository
{
    Task<IReadOnlyList<DetectedException>> SearchAsync(
        ExceptionFilter filter,
        CancellationToken cancellationToken);
    Task<DetectedException?> GetAsync(int id, CancellationToken cancellationToken);
    Task UpsertAsync(IEnumerable<DetectedException> exceptions, CancellationToken cancellationToken);
    Task UpdateReviewAsync(int id, ReviewDecision decision, CancellationToken cancellationToken);
}

public sealed record ExceptionFilter(
    DateOnly? FromDate = null,
    DateOnly? ThroughDate = null,
    string? Technician = null,
    ExceptionPriority? Priority = null,
    ReviewDecision? Status = null);

public interface IGeocodingService
{
    Task<GeoCoordinate?> GeocodeAsync(string address, CancellationToken cancellationToken);
}

public interface IDistanceCalculator
{
    double DistanceMetres(GeoCoordinate origin, GeoCoordinate destination);
}

public readonly record struct GeoCoordinate(double Latitude, double Longitude);

public interface ISynchronizationService
{
    Task<SynchronizationResult> SynchronizeAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken);
}

public interface IReadOnlyPilotService
{
    Task<ReadOnlyPilotResult> RunAsync(
        ReadOnlyPilotRequest request,
        CancellationToken cancellationToken);
}

public interface ISourceDataRepository
{
    Task UpsertTechniciansAsync(
        IEnumerable<Technician> technicians,
        CancellationToken cancellationToken);
    Task UpsertPerformancesAsync(
        IEnumerable<PlenionPerformance> performances,
        CancellationToken cancellationToken);
    Task UpsertTripsAsync(
        IEnumerable<PowerfleetTrip> trips,
        CancellationToken cancellationToken);
    Task AddSynchronizationRunAsync(
        SynchronizationRun run,
        CancellationToken cancellationToken);
}

public sealed record SynchronizationResult(
    int ImportedPlenionCount,
    int ImportedPowerfleetCount,
    int DetectedExceptionCount);

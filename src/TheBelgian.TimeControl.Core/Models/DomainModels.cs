using TheBelgian.TimeControl.Core.Interfaces;

namespace TheBelgian.TimeControl.Core.Models;

public sealed class Technician
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? MainGroup { get; set; }
    public string? Team { get; set; }
    public string? Function { get; set; }
    public string? Email { get; set; }
    public DateOnly? EmploymentStart { get; set; }
    public DateOnly? EmploymentEnd { get; set; }
    public int Kind { get; set; }
}

public sealed class Vehicle
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Plate { get; set; }
}

public sealed class VehicleAssignment
{
    public int Id { get; set; }
    public int TechnicianId { get; set; }
    public int VehicleId { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidUntil { get; set; }

    public bool IsValidOn(DateOnly date) =>
        date >= ValidFrom && (ValidUntil is null || date <= ValidUntil);
}

public sealed class PlenionPerformance
{
    public int Id { get; set; }
    public long ExternalId { get; set; }
    public string TechnicianExternalId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int BreakMinutes { get; set; }
    public decimal Kilometres { get; set; }
    public string? Description { get; set; }
    public string? ProjectExternalId { get; set; }
}

public sealed class PowerfleetTrip
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string? ObjectId { get; set; }
    public string? ObjectName { get; set; }
    public string? VehiclePlate { get; set; }
    public string? DriverId { get; set; }
    public string? DriverName { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int DurationMinutes { get; set; }
    public decimal DistanceKilometres { get; set; }
    public string? StartLocation { get; set; }
    public string? StartAddress { get; set; }
    public string? StartArea { get; set; }
    public string? StartAreaGroup { get; set; }
    public string? EndLocation { get; set; }
    public string? EndAddress { get; set; }
    public string? EndArea { get; set; }
    public string? EndAreaGroup { get; set; }
    public int? StoppedAfterMinutes { get; set; }
}

public sealed class PlenionWorkOrder
{
    public string ExternalId { get; set; } = string.Empty;
    public int TypeCode { get; set; }
    public string? Number { get; set; }
    public DateOnly? CreatedDate { get; set; }
    public DateOnly? CompletionDate { get; set; }
    public string? CustomerExternalId { get; set; }
    public string? ProjectCode { get; set; }
    public string? CompletionCode { get; set; }
    public string? ProjectExternalId { get; set; }
    public string? ContactExternalId { get; set; }
    public string? Memo { get; set; }
    public string? DeliveryAddressExternalId { get; set; }
    public string? Priority { get; set; }
}

public sealed class PlenionProject
{
    public string ExternalId { get; set; } = string.Empty;
    public string? Number { get; set; }
    public string? Name { get; set; }
    public string? CustomerExternalId { get; set; }
    public string? ContactExternalId { get; set; }
    public string? PlanningCode { get; set; }
}

public sealed class CustomerLocation
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double RadiusMetres { get; set; } = 150;
    public CustomerLocationType LocationType { get; set; }
}

public enum CustomerLocationType
{
    Unknown,
    Customer,
    Depot,
    Home,
}

public enum GeocodingStatus
{
    NotConfigured,
    NotProcessed,
    Geocoded,
    LowConfidence,
    Ambiguous,
    InvalidAddress,
    ProviderError,
}

public sealed record GeocodingCandidate(
    GeoCoordinate Coordinate,
    string? FormattedAddress,
    string? Confidence,
    string? EntityType,
    IReadOnlyList<string> MatchCodes);

public sealed record GeocodingResult(
    GeocodingStatus Status,
    string Provider,
    GeocodingCandidate? Primary,
    IReadOnlyList<GeocodingCandidate> Alternatives,
    string? ErrorMessage = null,
    bool FromCache = false);

public sealed class LocationResolutionCacheEntry
{
    public int Id { get; set; }
    public string? DeliveryAddressExternalId { get; set; }
    public string OriginalAddress { get; set; } = string.Empty;
    public string NormalizedAddress { get; set; } = string.Empty;
    public string AddressHash { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? ResolvedAddress { get; set; }
    public string? Confidence { get; set; }
    public string Provider { get; set; } = string.Empty;
    public GeocodingStatus Status { get; set; } = GeocodingStatus.NotProcessed;
    public string? ErrorMessage { get; set; }
    public string? AlternativesJson { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessfulResolutionAt { get; set; }
}

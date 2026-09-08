using System.ComponentModel.DataAnnotations;

namespace TheBelgian.TimeControl.Core.Configuration;

/// <summary>
/// Config-backed known work sites for display aliases (geofence). Display/context only.
/// </summary>
public sealed class KnownLocationsOptions
{
    public const string SectionName = "KnownLocations";

    public List<KnownLocationOptions> Locations { get; init; } = [];
}

public sealed class KnownLocationOptions
{
    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Locality { get; init; }

    [Range(-90, 90)]
    public double Latitude { get; init; }

    [Range(-180, 180)]
    public double Longitude { get; init; }

    [Range(1, 5_000)]
    public double RadiusMeters { get; init; }

    /// <summary>Optional secondary address line for admin UI.</summary>
    public string? SecondaryAddress { get; init; }
}

/// <summary>
/// Workbench UI diagnostics (separate from KnownLocations).
/// </summary>
public sealed class PayrollWorkbenchOptions
{
    public const string SectionName = "PayrollWorkbench";

    /// <summary>
    /// When true, production UI may show technical diagnostics. Prefer Development otherwise.
    /// </summary>
    public bool ShowDiagnostics { get; init; }
}

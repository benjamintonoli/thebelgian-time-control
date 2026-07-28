using System.ComponentModel.DataAnnotations;

namespace TheBelgian.TimeControl.Core.Configuration;

public sealed class GeocodingOptions
{
    public const string SectionName = "Geocoding";

    public string Provider { get; init; } = "AzureMaps";
    public string ApiKey { get; init; } = string.Empty;

    [RegularExpression("^[A-Za-z]{2}$")]
    public string CountryCode { get; init; } = "BE";

    public string Language { get; init; } = "nl-BE";
}

public sealed class LocationMatchingOptions
{
    public const string SectionName = "LocationMatching";

    [Range(1, 10_000)]
    public double StrongMatchMeters { get; init; } = 100;

    [Range(1, 10_000)]
    public double PossibleMatchMeters { get; init; } = 250;

    public void Validate()
    {
        if (StrongMatchMeters <= 0 ||
            PossibleMatchMeters <= StrongMatchMeters)
        {
            throw new InvalidOperationException(
                "Locatieafstandsgrenzen moeten positief en oplopend zijn.");
        }
    }
}

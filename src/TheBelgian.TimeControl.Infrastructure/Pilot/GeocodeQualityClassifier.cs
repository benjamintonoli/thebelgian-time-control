using System.Globalization;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class GeocodeQualityClassifier
{
    public static GeocodeQualityClass Classify(GeocodingResult geocoding)
    {
        if (geocoding.Status is GeocodingStatus.NotConfigured or
            GeocodingStatus.NotProcessed or
            GeocodingStatus.ProviderError or
            GeocodingStatus.InvalidAddress ||
            geocoding.Primary is null)
        {
            return GeocodeQualityClass.Unusable;
        }

        var primary = geocoding.Primary;
        var resultType = (primary.ResultTypeOrEntity() ?? string.Empty).ToLowerInvariant();
        var matchType = (primary.MatchType ??
                         (primary.MatchCodes.Count > 0 ? primary.MatchCodes[0] : null) ??
                         string.Empty).ToLowerInvariant();
        var confidence = ParseConfidence(primary.Confidence);
        var building = primary.ConfidenceBuildingLevel ?? 0;
        var street = primary.ConfidenceStreetLevel ?? 0;
        var city = primary.ConfidenceCityLevel ?? 0;

        if (geocoding.Status == GeocodingStatus.LowConfidence ||
            confidence < 0.5)
        {
            return confidence < 0.35
                ? GeocodeQualityClass.Unusable
                : GeocodeQualityClass.LowConfidence;
        }

        if (resultType is "building" or "amenity")
        {
            if (building >= 0.8 ||
                (confidence >= 0.9 && matchType.Contains("full", StringComparison.Ordinal)))
            {
                return resultType == "amenity"
                    ? GeocodeQualityClass.PreciseAmenity
                    : GeocodeQualityClass.PreciseBuilding;
            }

            if (building >= 0.5 || confidence >= 0.75)
            {
                return GeocodeQualityClass.PartialAddress;
            }
        }

        if (resultType is "street" or "suburb" or "district")
        {
            return street >= 0.7 || confidence >= 0.7
                ? GeocodeQualityClass.StreetOnly
                : GeocodeQualityClass.LowConfidence;
        }

        if (resultType is "postcode" or "city" or "county" or "state" or "country")
        {
            return GeocodeQualityClass.Unusable;
        }

        if (matchType.Contains("match_by_city", StringComparison.Ordinal) ||
            matchType.Contains("match_by_postcode", StringComparison.Ordinal))
        {
            return GeocodeQualityClass.Unusable;
        }

        if (matchType.Contains("match_by_street", StringComparison.Ordinal))
        {
            return GeocodeQualityClass.StreetOnly;
        }

        return confidence >= 0.85
            ? GeocodeQualityClass.PartialAddress
            : GeocodeQualityClass.LowConfidence;
    }

    public static bool CanUseAsPrecisePoint(GeocodeQualityClass quality) =>
        quality is GeocodeQualityClass.PreciseBuilding
            or GeocodeQualityClass.PreciseAmenity;

    public static double Score(GeocodeQualityClass quality) =>
        quality switch
        {
            GeocodeQualityClass.PreciseBuilding => 25,
            GeocodeQualityClass.PreciseAmenity => 23,
            GeocodeQualityClass.PartialAddress => 12,
            GeocodeQualityClass.StreetOnly => 4,
            GeocodeQualityClass.LowConfidence => 1,
            _ => 0,
        };

    private static double ParseConfidence(string? value) =>
        double.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;

    private static string? ResultTypeOrEntity(this GeocodingCandidate candidate) =>
        candidate.EntityType;
}

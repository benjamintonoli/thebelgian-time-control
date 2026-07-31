using System.Globalization;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

/// <summary>
/// Deterministic, local explanations. No OpenAI, Azure OpenAI, or other external LLM.
/// </summary>
public sealed class DeterministicReviewExplanationService : IReviewExplanationService
{
    public string Explain(SourceEvidence source, MatcherAssessment matcher)
    {
        if (string.IsNullOrWhiteSpace(source.PlenionAddress) ||
            matcher.GeocodeQuality == GeocodeQualityClass.Unusable)
        {
            return "Het Plenion-adres ontbreekt of is onbruikbaar; locatievergelijking is niet betrouwbaar.";
        }

        if (string.Equals(matcher.MatcherStatus, "Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return "Meerdere kandidaatbezoeken zijn vergelijkbaar sterk.";
        }

        var proposed = matcher.ProposedVisit;
        if (proposed is null)
        {
            if (matcher.CandidateVisits.Count == 0)
            {
                return "Er zijn geen kandidaatbezoeken beschikbaar voor deze prestatie.";
            }

            var nearest = matcher.CandidateVisits
                .OrderByDescending(item => item.OverlapMinutes)
                .ThenBy(item => item.DistanceMeters ?? double.MaxValue)
                .First();
            if (nearest.Arrival >= source.PlenionEnd)
            {
                return "De stop begint pas na het einde van de prestatie.";
            }

            if (nearest.Departure <= source.PlenionStart)
            {
                return "De stop eindigt vóór de start van de prestatie.";
            }

            return "Geen betrouwbaar voorstel: de beste kandidaat voldoet niet aan de acceptatiecriteria.";
        }

        if (proposed.Arrival >= source.PlenionEnd)
        {
            return "De stop begint pas na het einde van de prestatie.";
        }

        if (proposed.DistanceMeters is { } meters)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"De kandidaat ligt {meters:0} meter van het Plenion-adres en overlapt {proposed.OverlapPercent:0}%.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"De kandidaat overlapt {proposed.OverlapPercent:0}% met de prestatie; afstand is onbekend.");
    }
}

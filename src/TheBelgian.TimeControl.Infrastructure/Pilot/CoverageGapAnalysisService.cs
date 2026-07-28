using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class CoverageGapAnalysisService
{
    public static CoverageGapAnalysisResult Analyze(BroaderValidationResult broader)
    {
        var links = broader.Technicians
            .Where(item => item.Processed && item.Technician is not null)
            .Select(BuildEmployeeLink)
            .ToArray();
        var resolutions = broader.Technicians
            .Where(item => item.Processed && item.PilotResult is not null)
            .SelectMany(item => item.PilotResult!.LocationResolutions.Select(resolution =>
                (Technician: item.Technician!.Name, Resolution: resolution)))
            .ToArray();
        var breakdown = BuildBreakdown(resolutions.Select(item => item.Resolution).ToArray());
        var groups = GroupUnreliable(resolutions);
        var confirmable = groups
            .Where(group => group.AliasWouldMakeReliable)
            .OrderByDescending(group => group.PerformanceCount)
            .ThenBy(group => group.AverageDistanceMeters ?? double.MaxValue)
            .ToArray();
        var top20 = confirmable.Take(20).ToArray();
        var flippedIfAll = confirmable.Sum(group => group.PerformanceCount);
        var total = resolutions.Length;
        var reliable = breakdown.ReliableCount;
        var potentialPercent = total == 0
            ? 0
            : Math.Round(100d * (reliable + flippedIfAll) / total, 1);
        var projection = new CoverageGapAliasProjection(
            confirmable.Sum(group => group.PerformanceCount),
            groups.Where(group => !group.AliasWouldMakeReliable)
                .Sum(group => group.PerformanceCount),
            flippedIfAll,
            groups.Select(group => group.PlenionLocationKey).Distinct(StringComparer.Ordinal).Count(),
            confirmable.Length,
            potentialPercent,
            top20.Sum(group => group.PerformanceCount));
        return new CoverageGapAnalysisResult
        {
            LinkingModelDescription = LinkingModelText(),
            EmployeeLinks = links,
            MatchBreakdown = breakdown,
            UnreliableGroups = groups
                .OrderByDescending(group => group.PerformanceCount)
                .ToArray(),
            TopConfirmations = top20,
            AliasProjection = projection,
            AliasTableAdvice = BuildAliasAdvice(projection, breakdown),
        };
    }

    internal static CoverageGapEmployeeLink BuildEmployeeLink(
        BroaderValidationTechnicianResult technician)
    {
        var vehicles = technician.Days
            .SelectMany(day => day.Vehicles)
            .ToArray();
        var objectNames = vehicles
            .Select(vehicle => vehicle.ObjectName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var plates = vehicles
            .Select(vehicle => vehicle.ObjectPlate)
            .Where(plate => !string.IsNullOrWhiteSpace(plate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderBy(plate => plate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CoverageGapEmployeeLink(
            technician.Query,
            technician.Technician!.ExternalId,
            technician.Technician.Code,
            technician.Technician.Name,
            technician.DriverId ?? string.Empty,
            technician.DriverName,
            "Powerfleet.driverid (ontdekt via token-match Resource.OMSCHR ↔ drivername)",
            objectNames,
            plates);
    }

    internal static CoverageGapLocationGroup[] GroupUnreliable(
        IReadOnlyList<(string Technician, PilotLocationResolution Resolution)> resolutions)
    {
        var unreliable = resolutions
            .Where(item => !IsReliable(item.Resolution.MatchStatus))
            .Select(item =>
            {
                var best = item.Resolution.Candidates.Count > 0
                    ? item.Resolution.Candidates[0]
                    : null;
                return new
                {
                    item.Technician,
                    item.Resolution,
                    Best = best,
                    PlenionKey = PlenionLocationKey(item.Resolution),
                    StopKey = StopLocationKey(best),
                };
            })
            .GroupBy(
                item => item.PlenionKey + "||" + item.StopKey,
                StringComparer.Ordinal)
            .Select(group =>
            {
                var sample = group.First();
                var distances = group
                    .Select(item => item.Best?.DistanceMeters)
                    .Where(distance => distance is not null)
                    .Select(distance => distance!.Value)
                    .ToArray();
                var overlaps = group
                    .Select(item => item.Best?.TimeOverlapMinutes ?? 0)
                    .ToArray();
                var statuses = group
                    .GroupBy(item => item.Resolution.MatchStatus)
                    .OrderByDescending(item => item.Count())
                    .Select(item => item.Key)
                    .First();
                var reason = UncertaintyReason(sample.Resolution, sample.Best);
                var confirmable = IsConfirmableAlias(sample.Best);
                return new CoverageGapLocationGroup(
                    sample.PlenionKey,
                    sample.Resolution.DeliveryAddressExternalId,
                    sample.Resolution.OriginalAddress,
                    sample.Best?.Stop.Address,
                    sample.Best?.Stop.Latitude is null
                        ? null
                        : (double)sample.Best.Stop.Latitude.Value,
                    sample.Best?.Stop.Longitude is null
                        ? null
                        : (double)sample.Best.Stop.Longitude.Value,
                    group.Count(),
                    group.Select(item => item.Technician)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    distances.Length == 0 ? null : Math.Round(distances.Average(), 1),
                    overlaps.Length == 0 ? 0 : Math.Round(overlaps.Average(), 1),
                    statuses,
                    reason,
                    confirmable);
            })
            .OrderByDescending(group => group.PerformanceCount)
            .ThenBy(group => group.PlenionLocationKey, StringComparer.Ordinal)
            .ToArray();
        return unreliable;
    }

    /// <summary>
    /// Een beheerder bevestigt alleen een stop/parking die geografisch bij de
    /// Plenion-locatie kan horen (campus/parking/toegang), niet een willekeurige verre stop.
    /// </summary>
    private const double MaximumAliasDistanceMeters = 500;

    internal static bool IsConfirmableAlias(PilotLocationCandidateScore? best) =>
        best?.DistanceMeters is not null &&
        best.DistanceMeters.Value <= MaximumAliasDistanceMeters;

    private static CoverageGapMatchBreakdown BuildBreakdown(
        PilotLocationResolution[] resolutions)
    {
        var confirmed = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.ConfirmedLocationMatch);
        var probable = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.ProbableLocationMatch);
        var manual = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.ManualReviewRequired);
        var none = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.NoReliableMatch);
        var address = resolutions.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.AddressDataIssue);
        var total = resolutions.Length;
        var reliable = confirmed + probable;
        double Percent(int count) =>
            total == 0 ? 0 : Math.Round(100d * count / total, 1);
        return new CoverageGapMatchBreakdown(
            total,
            confirmed,
            probable,
            manual,
            none,
            address,
            reliable,
            Percent(reliable),
            total - reliable,
            Percent(total - reliable),
            PrimaryCause(none, manual, address, probable, confirmed));
    }

    private static string PrimaryCause(
        int none,
        int manual,
        int address,
        int probable,
        int confirmed)
    {
        var parts = new List<string>();
        if (none > 0)
        {
            parts.Add(
                $"{none} NoReliableMatch (geen sterke stopkandidaat of zwakke score)");
        }

        if (manual > 0)
        {
            parts.Add(
                $"{manual} ManualReviewRequired (concurrente stops of ambigue geocoding)");
        }

        if (address > 0)
        {
            parts.Add($"{address} AddressDataIssue");
        }

        parts.Add(
            $"Betrouwbaar nu: {confirmed} Confirmed + {probable} Probable = {confirmed + probable}.");
        parts.Add(
            "Objectname/kenteken speelt geen rol in de matchstatus; de 38,6% komt uit locatie-/tijdscoring.");
        return string.Join(" ", parts);
    }

    private static string UncertaintyReason(
        PilotLocationResolution resolution,
        PilotLocationCandidateScore? best)
    {
        if (best is null)
        {
            return "Geen Powerfleet-stopkandidaat met coördinaten; alias kan deze prestatie niet redden.";
        }

        if (resolution.MatchStatus == PilotLocationResolutionStatus.ManualReviewRequired)
        {
            return resolution.DiagnosticCategory +
                   "; topkandidaten liggen te dicht bij elkaar of geocoding is ambigu.";
        }

        if (resolution.MatchStatus == PilotLocationResolutionStatus.AddressDataIssue)
        {
            return "Adreskwaliteit onvoldoende voor automatische bevestiging: " +
                   resolution.DiagnosticCategory;
        }

        if (best.DistanceClassification == PilotDistanceClassification.LocationMismatch)
        {
            return "Afstand buiten PossibleMatchMeters; vermoedelijk parking/toegang of ander adres (" +
                   resolution.DiagnosticCategory + ").";
        }

        if (best.TimeOverlapMinutes <= 0 && best.TimeScore <= 0)
        {
            return "Onvoldoende tijdsoverlap tussen Plenion-prestatie en Powerfleet-stop.";
        }

        return resolution.DiagnosticCategory + "; score onder Confirmed/Probable-drempel.";
    }

    private static bool IsReliable(PilotLocationResolutionStatus status) =>
        status is PilotLocationResolutionStatus.ConfirmedLocationMatch
            or PilotLocationResolutionStatus.ProbableLocationMatch;

    private static string PlenionLocationKey(PilotLocationResolution resolution)
    {
        if (!string.IsNullOrWhiteSpace(resolution.DeliveryAddressExternalId))
        {
            return "LACLEUNIK:" + resolution.DeliveryAddressExternalId.Trim();
        }

        return "ADDR:" + (string.IsNullOrWhiteSpace(resolution.AddressHash)
            ? resolution.NormalizedAddress
            : resolution.AddressHash);
    }

    private static string StopLocationKey(PilotLocationCandidateScore? best)
    {
        if (best is null)
        {
            return "NO_STOP";
        }

        if (best.Stop.Latitude is not null && best.Stop.Longitude is not null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"GEO:{best.Stop.Latitude:0.000}:{best.Stop.Longitude:0.000}");
        }

        return "STOP:" + (best.Stop.Address ?? best.Stop.StopId);
    }

    private static string LinkingModelText() =>
        """
        Plenion-medewerkerzoektocht: Resource WHERE SOORT=1 AND (OMSCHR LIKE %query% OR RESCODE = query) → IDRESOURCE, RESCODE, OMSCHR.
        Powerfleet-bestuurderdetectie: token-match van Resource.OMSCHR op trip.drivername; kies meest frequente trip.driverid (ritten zonder driverid tellen niet).
        Effectieve ritkoppeling in bredere validatie: uitsluitend exacte gelijkheid op Powerfleet driverid. drivername is ontdekkingssleutel; objectid/objectname/objectPlate zijn informatief (bv. FDE/JDE/JDS) en bepalen de koppeling niet.
        MissingDriver: rit zonder driverid → niet gebruikt voor uren- of locatieconclusies.
        """;

    private static string BuildAliasAdvice(
        CoverageGapAliasProjection projection,
        CoverageGapMatchBreakdown breakdown)
    {
        if (projection.UniqueConfirmableAliases == 0)
        {
            return "KnownLocationAlias is nu onvoldoende: er zijn geen bevestigbare Plenion↔stop-paren met kandidaten.";
        }

        var remaining = projection.UnreliableWithoutCandidateStop;
        var builder = new StringBuilder();
        builder.Append(
            "Een eenvoudige KnownLocationAlias-tabel (Plenion LACLEUNIK/adres ↔ Powerfleet-stop geo/adres) is voldoende als eerste stap: ");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"{projection.UniqueConfirmableAliases} bevestigbare paren zouden {projection.PerformancesFlippedIfAllAliasesConfirmed} nu onbetrouwbare prestaties betrouwbaar maken ");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"(potentiële betrouwbare graad {projection.PotentialReliablePercentAfterAliasConfirmation}%, nu {breakdown.ReliablePercent}%). ");
        if (remaining > 0)
        {
            builder.Append(
                CultureInfo.InvariantCulture,
                $"Nog {remaining} prestaties blijven open zonder stopkandidaat en vragen aparte datakwaliteit of tijdvenster-analyse, geen alias alleen.");
        }
        else
        {
            builder.Append(
                "Alle huidige onbetrouwbare prestaties met een stopkandidaat zijn in principe aliasbaar.");
        }

        return builder.ToString();
    }
}

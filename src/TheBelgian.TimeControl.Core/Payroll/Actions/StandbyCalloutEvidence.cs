using System.Globalization;
using System.Text.RegularExpressions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Determines whether proposed standby VAN/TOT bounds represent a complete
/// physical callout (home departure → site → home return), not a travel fragment.
/// </summary>
public static class StandbyCalloutEvidence
{
    public static readonly TimeSpan BoundaryTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Booked start before physical departure by more than this → possible phone-then-physical.
    /// </summary>
    public static readonly TimeSpan HybridLeadThreshold = TimeSpan.FromMinutes(10);

    /// <summary>Maximum payable telephone standby allowance (standalone phone-only rule).</summary>
    public static readonly TimeSpan MaxTelephoneAllowance = TimeSpan.FromMinutes(15);

    public static StandbyCalloutAssessment Assess(
        DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd,
        DateTimeOffset currentStart,
        DateTimeOffset currentEnd,
        IReadOnlyList<StandbyGpsTripEvidence> dayTrips)
    {
        if (proposedEnd <= proposedStart)
        {
            return StandbyCalloutAssessment.Blocked(
                PayrollActionBlockReasonCode.IncompleteCallout,
                "Voorstelduur is niet positief; geen complete wachtdienst-callout.");
        }

        var windowTrips = SelectWindowTrips(dayTrips, proposedStart, proposedEnd);
        if (windowTrips.Count == 0)
        {
            return StandbyCalloutAssessment.Blocked(
                PayrollActionBlockReasonCode.IncompleteCallout,
                "Geen betekenisvolle GPS-ritten in het voorgestelde callout-venster.");
        }

        if (windowTrips.Count == 1)
        {
            return StandbyCalloutAssessment.Blocked(
                PayrollActionBlockReasonCode.IncompleteCallout,
                "Slechts één GPS-rit (outbound/arrival); geen bewezen heen-en-terug callout.");
        }

        var first = windowTrips[0];
        var last = windowTrips[^1];
        if (!Near(first.Start, proposedStart) || !Near(last.End, proposedEnd))
        {
            return StandbyCalloutAssessment.Blocked(
                PayrollActionBlockReasonCode.IncompleteCallout,
                "GPS-grenzen sluiten niet aan op voorgestelde start/einde van de callout.");
        }

        var siteMatch = LocalityCompatible(first.EndAddress, last.StartAddress);
        var homeMatch = LocalityCompatible(first.StartAddress, last.EndAddress);
        if (!siteMatch || !homeMatch)
        {
            // Multi-leg chains often fail A→B / B→A; treat as ambiguous unless clear home return.
            if (!homeMatch)
            {
                return StandbyCalloutAssessment.Blocked(
                    PayrollActionBlockReasonCode.IntermediateStopEnd,
                    "Voorstel-einde is geen bewezen thuiskomst (laatste rit eindigt niet in vertrek-localiteit).");
            }

            return StandbyCalloutAssessment.Blocked(
                PayrollActionBlockReasonCode.MultiLegAmbiguous,
                "GPS-keten is multi-leg/ambigu; site heen-terug niet deterministisch.");
        }

        if (windowTrips.Count > 2)
        {
            // Extra legs between home out and home return: only OK if first/last still form home↔site.
            // Intermediate stops at same site locality are acceptable.
            var intermediateAwayFromSite = windowTrips
                .Skip(1)
                .Take(windowTrips.Count - 2)
                .Any(trip =>
                    !LocalityCompatible(trip.StartAddress, first.EndAddress)
                    && !LocalityCompatible(trip.EndAddress, first.EndAddress)
                    && !LocalityCompatible(trip.EndAddress, first.StartAddress));
            if (intermediateAwayFromSite)
            {
                return StandbyCalloutAssessment.Blocked(
                    PayrollActionBlockReasonCode.MultiLegAmbiguous,
                    "Tussenliggende GPS-ritten wijken af van de callout (multi-leg ambigu).");
            }
        }

        var startChanges = !Near(currentStart, proposedStart);
        var endChanges = !Near(currentEnd, proposedEnd);
        if (startChanges && !endChanges)
        {
            // Unchanged end must independently be the return of this callout.
            if (!Near(currentEnd, last.End))
            {
                return StandbyCalloutAssessment.Blocked(
                    PayrollActionBlockReasonCode.IncompleteCallout,
                    "Alleen start wijzigt, maar bestaande einde is niet de bewezen thuiskomst van dezelfde callout.");
            }
        }

        if (endChanges && !startChanges)
        {
            if (!Near(currentStart, first.Start))
            {
                return StandbyCalloutAssessment.Blocked(
                    PayrollActionBlockReasonCode.IncompleteCallout,
                    "Alleen einde wijzigt, maar bestaande start is niet het bewezen fysieke vertrek van dezelfde callout.");
            }
        }

        var evidence =
            $"callout=complete; trips={windowTrips.Count}; " +
            $"out={FormatTrip(first)}; back={FormatTrip(last)}; " +
            $"homeLocality={ExtractLocality(first.StartAddress) ?? "—"}; " +
            $"siteLocality={ExtractLocality(first.EndAddress) ?? "—"}";

        // Hybrid safety: booked start before proven physical departure may include telephone standby.
        // Do not auto-cut VAN to GPS departure — that can remove valid phone time and cannot
        // represent non-contiguous phone+physical in a single VAN/TOT without paying the gap.
        if (IsPossiblePhoneThenPhysical(currentStart, first.Start, dayTrips))
        {
            var phoneLead = first.Start - currentStart;
            var hybridNote =
                $"{evidence} | hybrid=PossiblePhoneThenPhysical; " +
                $"bookedStart={currentStart:HH:mm}; physicalDeparture={first.Start:HH:mm}; " +
                $"phoneLead={phoneLead.TotalMinutes:0}min; maxPhoneAllowance={MaxTelephoneAllowance.TotalMinutes:0}min; " +
                "scenario=indien telefonisch contact bevestigd wordt: max 0:15 + fysieke callout (niet-aaneengesloten; split vereist)";
            return new StandbyCalloutAssessment(
                IsComplete: false,
                PayrollActionBlockReasonCode.PossiblePhoneThenPhysical,
                "Mogelijke telefonische wachtdienst vóór fysiek vertrek. Eerst bevestigen hoe de interventie opgebouwd is. "
                + "Eenvoudige VAN/TOT-correctie naar alleen GPS-vertrek is geblokkeerd (REQUIRES_SPLIT_OR_PROVEN_BOOKING_METHOD).",
                hybridNote,
                first.Start,
                last.End,
                IsPossiblePhoneThenPhysical: true,
                MaxTelephoneAllowanceMinutes: (decimal)MaxTelephoneAllowance.TotalMinutes,
                BookedStart: currentStart,
                BookedEnd: currentEnd);
        }

        return new StandbyCalloutAssessment(
            IsComplete: true,
            PayrollActionBlockReasonCode.None,
            null,
            evidence,
            first.Start,
            last.End);
    }

    /// <summary>
    /// Deterministic hybrid candidate: booked start materially precedes physical departure and
    /// pre-departure window has no meaningful GPS movement proving on-site/travel work.
    /// Does not claim a phone call definitely occurred.
    /// </summary>
    public static bool IsPossiblePhoneThenPhysical(
        DateTimeOffset bookedStart,
        DateTimeOffset physicalDeparture,
        IReadOnlyList<StandbyGpsTripEvidence> dayTrips)
    {
        if (physicalDeparture - bookedStart <= HybridLeadThreshold)
        {
            return false;
        }

        var preTrips = dayTrips
            .Where(trip => trip.Start < physicalDeparture && trip.End > bookedStart)
            .Where(trip =>
                trip.DistanceKilometres >= StandbyControl.MeaningfulMovementKm
                || trip.DrivingMinutes >= StandbyControl.MeaningfulDrivingMinutes)
            .Where(trip => trip.End <= physicalDeparture + BoundaryTolerance)
            .ToList();

        // Any meaningful movement before the outbound departure disproves a quiet phone-only lead.
        return preTrips.Count == 0
            || preTrips.All(trip => Near(trip.Start, physicalDeparture) || trip.Start >= physicalDeparture - BoundaryTolerance);
    }

    public static List<StandbyGpsTripEvidence> SelectWindowTrips(
        IReadOnlyList<StandbyGpsTripEvidence> dayTrips,
        DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd)
    {
        var pad = StandbyControl.MatchWindowPadding;
        var start = proposedStart - pad;
        var end = proposedEnd + pad;
        return dayTrips
            .Where(trip => trip.Start < end && trip.End > start)
            .Where(trip =>
                trip.DistanceKilometres >= StandbyControl.MeaningfulMovementKm
                || trip.DrivingMinutes >= StandbyControl.MeaningfulDrivingMinutes)
            .Where(trip => trip.Start < proposedEnd && trip.End > proposedStart)
            .OrderBy(trip => trip.Start)
            .ToList();
    }

    public static bool Near(DateTimeOffset a, DateTimeOffset b) =>
        Math.Abs((a - b).TotalMinutes) <= BoundaryTolerance.TotalMinutes;

    public static bool LocalityCompatible(string? left, string? right)
    {
        var a = ExtractLocality(left);
        var b = ExtractLocality(right);
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Street-level fallback when locality parse fails partially.
        var streetA = ExtractStreetStem(left);
        var streetB = ExtractStreetStem(right);
        return !string.IsNullOrWhiteSpace(streetA)
            && string.Equals(streetA, streetB, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractLocality(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        // "Street 12, 1880 Kapelle-op-den-Bos, België"
        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var match = Regex.Match(part, @"^\d{4}\s+(.+)$");
            if (match.Success)
            {
                return NormalizeToken(match.Groups[1].Value);
            }
        }

        if (parts.Length >= 2)
        {
            return NormalizeToken(parts[^2]);
        }

        return NormalizeToken(address);
    }

    public static string? ExtractStreetStem(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var first = address.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        var stem = Regex.Replace(first, @"\s+\d+[a-zA-Z]?$", string.Empty);
        return NormalizeToken(stem);
    }

    private static string NormalizeToken(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string FormatTrip(StandbyGpsTripEvidence trip) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{trip.Start:HH:mm}-{trip.End:HH:mm}:{ExtractLocality(trip.StartAddress) ?? "?"}->{ExtractLocality(trip.EndAddress) ?? "?"}");
}

public sealed record StandbyCalloutAssessment(
    bool IsComplete,
    PayrollActionBlockReasonCode BlockReasonCode,
    string? BlockReason,
    string EvidenceNote,
    DateTimeOffset? OutboundStart = null,
    DateTimeOffset? ReturnEnd = null,
    bool IsPossiblePhoneThenPhysical = false,
    decimal? MaxTelephoneAllowanceMinutes = null,
    DateTimeOffset? BookedStart = null,
    DateTimeOffset? BookedEnd = null)
{
    public static StandbyCalloutAssessment Blocked(
        PayrollActionBlockReasonCode code,
        string reason) =>
        new(false, code, reason, reason);
}

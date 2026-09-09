using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Classifies pairwise ordinary overlaps and proposes human-approved Adjust/Delete targets.
/// Evidence-only; never mutates Plenion.
/// </summary>
public static class OverlapIntelligence
{
    /// <summary>Skip noisy sub-minute pairwise overlaps (reuse existing tolerance intent).</summary>
    public static readonly decimal MinimumOverlapHours = 1m / 60m;

    public static OverlapAnalysis Analyze(
        NormalizedPerformanceEntry left,
        NormalizedPerformanceEntry right,
        StandbyGpsDayEvidence? gps = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var ordered = OrderPair(left, right);
        var a = ordered.A;
        var b = ordered.B;
        var window = CalculateWindow(a, b);
        if (window.OverlapHours < MinimumOverlapHours)
        {
            return OverlapAnalysis.Empty(a, b);
        }

        var kind = ClassifyKind(a, b);
        var gpsAdvice = AnalyzeGps(a, b, window, gps);
        var recommendation = Recommend(kind, a, b, window, gpsAdvice);
        var confidence = ResolveConfidence(kind, recommendation, gpsAdvice);
        var adviceNl = BuildAdvice(kind, recommendation, a, b, window, gpsAdvice);
        var evidence = BuildEvidence(kind, recommendation, confidence, a, b, window, gpsAdvice);

        return new OverlapAnalysis(
            A: a,
            B: b,
            Kind: kind,
            OverlapStart: window.Start,
            OverlapEnd: window.End,
            OverlapHours: window.OverlapHours,
            OverlapMinutes: Math.Round(window.OverlapHours * 60m, 0, MidpointRounding.AwayFromZero),
            Recommendation: recommendation.Action,
            TargetPerformanceId: recommendation.TargetPerformanceId,
            ProposedStart: recommendation.ProposedStart,
            ProposedEnd: recommendation.ProposedEnd,
            Confidence: confidence,
            AdviceNl: adviceNl,
            GpsSupport: gpsAdvice.Support,
            Evidence: evidence);
    }

    public static OverlapKind ClassifyKind(
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b)
    {
        if (IsTravelOrWorkPair(a, b))
        {
            return OverlapKind.TravelWorkOverlap;
        }

        if (IsStandbyOrSpecial(a) || IsStandbyOrSpecial(b))
        {
            return OverlapKind.StandbySpecialOverlap;
        }

        if (IsExactDuplicate(a, b))
        {
            return OverlapKind.ExactDuplicate;
        }

        if (SameJob(a, b))
        {
            return OverlapKind.PartialSameJob;
        }

        if (DifferentJobs(a, b))
        {
            return OverlapKind.PartialDifferentJobs;
        }

        return OverlapKind.Ambiguous;
    }

    public static long PreferDuplicateDeleteTarget(
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b)
    {
        var aEmpty = string.IsNullOrWhiteSpace(a.Description) && string.IsNullOrWhiteSpace(a.Memo);
        var bEmpty = string.IsNullOrWhiteSpace(b.Description) && string.IsNullOrWhiteSpace(b.Memo);
        if (aEmpty != bEmpty)
        {
            return aEmpty ? a.SourceEntryId : b.SourceEntryId;
        }

        // Later-created / higher source id is the accidental duplicate when times match.
        return Math.Max(a.SourceEntryId, b.SourceEntryId);
    }

    private static (NormalizedPerformanceEntry A, NormalizedPerformanceEntry B) OrderPair(
        NormalizedPerformanceEntry left,
        NormalizedPerformanceEntry right) =>
        left.SourceEntryId <= right.SourceEntryId
            ? (left, right)
            : (right, left);

    private static OverlapWindow CalculateWindow(
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b)
    {
        var start = a.Start!.Value > b.Start!.Value ? a.Start.Value : b.Start.Value;
        var end = a.End!.Value < b.End!.Value ? a.End.Value : b.End.Value;
        if (end <= start)
        {
            return new OverlapWindow(start, end, 0m);
        }

        return new OverlapWindow(start, end, (decimal)(end - start).TotalHours);
    }

    private static bool IsExactDuplicate(
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b)
    {
        if (a.Start is null || a.End is null || b.Start is null || b.End is null)
        {
            return false;
        }

        var startDelta = Math.Abs((a.Start.Value - b.Start.Value).TotalMinutes);
        var endDelta = Math.Abs((a.End.Value - b.End.Value).TotalMinutes);
        if (startDelta > 1d || endDelta > 1d)
        {
            return false;
        }

        if (a.HfdTaakId != b.HfdTaakId)
        {
            return false;
        }

        if (!SameProject(a, b))
        {
            return false;
        }

        var aBon = NormalizeBon(a.BonNr);
        var bBon = NormalizeBon(b.BonNr);
        if (aBon is null && bBon is null)
        {
            return true;
        }

        return string.Equals(aBon, bBon, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameJob(NormalizedPerformanceEntry a, NormalizedPerformanceEntry b)
    {
        var aBon = NormalizeBon(a.BonNr);
        var bBon = NormalizeBon(b.BonNr);
        if (aBon is not null && bBon is not null
            && string.Equals(aBon, bBon, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SameProject(a, b) && aBon is null && bBon is null;
    }

    private static bool DifferentJobs(NormalizedPerformanceEntry a, NormalizedPerformanceEntry b)
    {
        var aBon = NormalizeBon(a.BonNr);
        var bBon = NormalizeBon(b.BonNr);
        if (aBon is not null && bBon is not null
            && !string.Equals(aBon, bBon, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !SameProject(a, b);
    }

    private static bool SameProject(NormalizedPerformanceEntry a, NormalizedPerformanceEntry b)
    {
        if (a.ProjectNumber is not null && b.ProjectNumber is not null)
        {
            return a.ProjectNumber == b.ProjectNumber;
        }

        if (!string.IsNullOrWhiteSpace(a.ProjectId) && !string.IsNullOrWhiteSpace(b.ProjectId))
        {
            return string.Equals(a.ProjectId, b.ProjectId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsTravelOrWorkPair(NormalizedPerformanceEntry a, NormalizedPerformanceEntry b) =>
        IsTravel(a) ^ IsTravel(b);

    private static bool IsTravel(NormalizedPerformanceEntry entry) =>
        entry.IsTravel || entry.HfdTaakId == 5;

    private static bool IsStandbyOrSpecial(NormalizedPerformanceEntry entry) =>
        entry.IsStandby || entry.IsAbsence || entry.HfdTaakId is 10 or 18 or 23;

    private static string? NormalizeBon(string? bon) =>
        string.IsNullOrWhiteSpace(bon) ? null : bon.Trim();

    private static GpsAdvice AnalyzeGps(
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b,
        OverlapWindow window,
        StandbyGpsDayEvidence? gps)
    {
        if (gps is null || !gps.HasVehicleMapping || gps.MappingAmbiguous || !gps.HasUsableTrips)
        {
            return GpsAdvice.None;
        }

        var trips = gps.Trips.OrderBy(item => item.Start).ToList();
        var overlapMid = window.Start + TimeSpan.FromTicks((window.End - window.Start).Ticks / 2);

        // Prefer B-trim when B starts inside A and first trip after A's start ends at/after overlap mid
        // (arrival chronology suggests B started before presence).
        if (b.Start is not null
            && a.Start is not null
            && a.End is not null
            && b.Start.Value > a.Start.Value
            && b.Start.Value < a.End.Value)
        {
            var arrivalAfterA = trips
                .Where(trip => trip.End >= window.Start && trip.End <= (b.End ?? a.End.Value).AddMinutes(30))
                .OrderByDescending(trip => trip.End)
                .FirstOrDefault();
            if (arrivalAfterA is not null
                && arrivalAfterA.End > b.Start.Value.AddMinutes(5)
                && arrivalAfterA.End < (b.End ?? arrivalAfterA.End))
            {
                return new GpsAdvice(
                    OverlapGpsSupport.SupportsB,
                    "B",
                    arrivalAfterA.End,
                    b.End,
                    $"GPS/aankomst rond {arrivalAfterA.End:HH:mm} ondersteunt latere start van prestatie B.");
            }
        }

        if (a.Start is not null
            && b.Start is not null
            && b.End is not null
            && a.End is not null
            && a.End.Value > b.Start.Value
            && a.End.Value < b.End.Value)
        {
            var departureBeforeB = trips
                .Where(trip => trip.Start >= a.Start.Value.AddMinutes(-30) && trip.Start <= window.End.AddMinutes(30))
                .OrderByDescending(trip => trip.Start)
                .FirstOrDefault();
            if (departureBeforeB is not null
                && departureBeforeB.Start < a.End.Value.AddMinutes(-5)
                && departureBeforeB.Start > a.Start.Value)
            {
                return new GpsAdvice(
                    OverlapGpsSupport.SupportsA,
                    "A",
                    a.Start,
                    departureBeforeB.Start,
                    $"GPS/vertrek rond {departureBeforeB.Start:HH:mm} ondersteunt vroegere stop van prestatie A.");
            }
        }

        var tripsInOverlap = trips.Count(trip => trip.Start < window.End && trip.End > window.Start);
        if (tripsInOverlap == 0)
        {
            return new GpsAdvice(
                OverlapGpsSupport.Ambiguous,
                null,
                null,
                null,
                "GPS toont geen duidelijke chronologie in het overlapvenster.");
        }

        _ = overlapMid;
        return new GpsAdvice(
            OverlapGpsSupport.Ambiguous,
            null,
            null,
            null,
            "GPS is aanwezig maar ondersteunt geen eenduidige grenscorrectie.");
    }

    private static Recommendation Recommend(
        OverlapKind kind,
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b,
        OverlapWindow window,
        GpsAdvice gps)
    {
        if (kind == OverlapKind.ExactDuplicate)
        {
            return new Recommendation(
                OverlapRecommendationAction.DeleteDuplicate,
                PreferDuplicateDeleteTarget(a, b),
                null,
                null);
        }

        if (gps.Support is OverlapGpsSupport.SupportsA or OverlapGpsSupport.SupportsB
            && gps.ProposedStart is not null
            && gps.ProposedEnd is not null
            && gps.ProposedEnd > gps.ProposedStart
            && gps.TargetSide is not null)
        {
            var targetId = gps.TargetSide == "A" ? a.SourceEntryId : b.SourceEntryId;
            return new Recommendation(
                OverlapRecommendationAction.AdjustBoundary,
                targetId,
                gps.ProposedStart,
                gps.ProposedEnd);
        }

        if (kind is OverlapKind.PartialSameJob or OverlapKind.PartialDifferentJobs)
        {
            // Without GPS/Track & Trace, do not invent a boundary — workbench may still prefill manually.
            return new Recommendation(OverlapRecommendationAction.Uncertain, null, null, null);
        }

        if (kind == OverlapKind.TravelWorkOverlap)
        {
            return new Recommendation(OverlapRecommendationAction.Uncertain, null, null, null);
        }

        _ = window;
        return new Recommendation(OverlapRecommendationAction.Uncertain, null, null, null);
    }

    private static OverlapConfidence ResolveConfidence(
        OverlapKind kind,
        Recommendation recommendation,
        GpsAdvice gps)
    {
        if (kind == OverlapKind.ExactDuplicate
            && recommendation.Action == OverlapRecommendationAction.DeleteDuplicate)
        {
            return OverlapConfidence.High;
        }

        if (recommendation.Action == OverlapRecommendationAction.AdjustBoundary
            && gps.Support is OverlapGpsSupport.SupportsA or OverlapGpsSupport.SupportsB)
        {
            return OverlapConfidence.High;
        }

        if (recommendation.Action == OverlapRecommendationAction.AdjustBoundary
            && kind == OverlapKind.PartialSameJob)
        {
            return OverlapConfidence.Review;
        }

        return OverlapConfidence.Ambiguous;
    }

    private static string BuildAdvice(
        OverlapKind kind,
        Recommendation recommendation,
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b,
        OverlapWindow window,
        GpsAdvice gps)
    {
        if (kind == OverlapKind.ExactDuplicate)
        {
            return "Deze twee prestaties lijken dezelfde boeking te zijn.";
        }

        if (recommendation.Action == OverlapRecommendationAction.AdjustBoundary
            && recommendation.TargetPerformanceId is not null
            && recommendation.ProposedStart is not null
            && recommendation.ProposedEnd is not null)
        {
            var side = recommendation.TargetPerformanceId == a.SourceEntryId ? "A" : "B";
            var minutes = (int)Math.Round(window.OverlapHours * 60m, MidpointRounding.AwayFromZero);
            var gpsNote = string.IsNullOrWhiteSpace(gps.Note) ? string.Empty : " " + gps.Note;
            return $"Prestatie {side} lijkt bijgestuurd te moeten worden tot {recommendation.ProposedStart:HH:mm}–{recommendation.ProposedEnd:HH:mm} (overlap {minutes} min).{gpsNote}";
        }

        if (kind == OverlapKind.TravelWorkOverlap)
        {
            return "Reis- en werkprestatie overlappen; controleer welke boeking fout is.";
        }

        if (kind == OverlapKind.PartialDifferentJobs)
        {
            return "Gedeeltelijke overlap tussen verschillende jobs; Track & Trace kan de grens verklaren.";
        }

        if (kind == OverlapKind.PartialSameJob)
        {
            return "Gedeeltelijke overlap op dezelfde job; grenscorrectie of duplicaat mogelijk.";
        }

        return "Overlap is aanwezig maar het bewijs is onvoldoende voor een harde correctie.";
    }

    private static string BuildEvidence(
        OverlapKind kind,
        Recommendation recommendation,
        OverlapConfidence confidence,
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b,
        OverlapWindow window,
        GpsAdvice gps)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"kind={kind}; confidence={confidence}; ");
        sb.Append(CultureInfo.InvariantCulture,
            $"overlap={window.Start:HH:mm}-{window.End:HH:mm}; overlapMin={(int)Math.Round(window.OverlapHours * 60m)}; ");
        sb.Append(CultureInfo.InvariantCulture,
            $"rec={recommendation.Action}; target={recommendation.TargetPerformanceId?.ToString(CultureInfo.InvariantCulture) ?? "—"}; ");
        if (recommendation.ProposedStart is not null && recommendation.ProposedEnd is not null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"proposed={recommendation.ProposedStart:HH:mm}-{recommendation.ProposedEnd:HH:mm}; ");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"A#{a.SourceEntryId} {a.Start:HH:mm}-{a.End:HH:mm} proj={FormatProject(a)} bon={NormalizeBon(a.BonNr) ?? "—"} hfd={a.HfdTaakId?.ToString(CultureInfo.InvariantCulture) ?? "—"} desc={Trim(a.Description)}; ");
        sb.Append(CultureInfo.InvariantCulture,
            $"B#{b.SourceEntryId} {b.Start:HH:mm}-{b.End:HH:mm} proj={FormatProject(b)} bon={NormalizeBon(b.BonNr) ?? "—"} hfd={b.HfdTaakId?.ToString(CultureInfo.InvariantCulture) ?? "—"} desc={Trim(b.Description)}; ");
        sb.Append(CultureInfo.InvariantCulture, $"gpsSupport={gps.Support}");
        if (!string.IsNullOrWhiteSpace(gps.Note))
        {
            sb.Append(CultureInfo.InvariantCulture, $"; gpsNote={gps.Note}");
        }

        return sb.ToString();
    }

    private static string FormatProject(NormalizedPerformanceEntry entry) =>
        entry.ProjectNumber?.ToString(CultureInfo.InvariantCulture)
        ?? entry.ProjectId
        ?? "—";

    private static string Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private readonly record struct OverlapWindow(DateTimeOffset Start, DateTimeOffset End, decimal OverlapHours);

    private readonly record struct Recommendation(
        OverlapRecommendationAction Action,
        long? TargetPerformanceId,
        DateTimeOffset? ProposedStart,
        DateTimeOffset? ProposedEnd);

    private readonly record struct GpsAdvice(
        OverlapGpsSupport Support,
        string? TargetSide,
        DateTimeOffset? ProposedStart,
        DateTimeOffset? ProposedEnd,
        string? Note)
    {
        public static GpsAdvice None { get; } = new(OverlapGpsSupport.None, null, null, null, null);
    }
}

public enum OverlapKind
{
    ExactDuplicate = 0,
    PartialSameJob = 1,
    PartialDifferentJobs = 2,
    TravelWorkOverlap = 3,
    StandbySpecialOverlap = 4,
    Ambiguous = 5,
}

public enum OverlapRecommendationAction
{
    None = 0,
    DeleteDuplicate = 1,
    AdjustBoundary = 2,
    BothCorrect = 3,
    Uncertain = 4,
}

public enum OverlapConfidence
{
    High = 0,
    Review = 1,
    Ambiguous = 2,
}

public enum OverlapGpsSupport
{
    None = 0,
    SupportsA = 1,
    SupportsB = 2,
    Ambiguous = 3,
}

public sealed record OverlapAnalysis(
    NormalizedPerformanceEntry A,
    NormalizedPerformanceEntry B,
    OverlapKind Kind,
    DateTimeOffset OverlapStart,
    DateTimeOffset OverlapEnd,
    decimal OverlapHours,
    decimal OverlapMinutes,
    OverlapRecommendationAction Recommendation,
    long? TargetPerformanceId,
    DateTimeOffset? ProposedStart,
    DateTimeOffset? ProposedEnd,
    OverlapConfidence Confidence,
    string AdviceNl,
    OverlapGpsSupport GpsSupport,
    string Evidence)
{
    public static OverlapAnalysis Empty(
        NormalizedPerformanceEntry a,
        NormalizedPerformanceEntry b) =>
        new(
            a,
            b,
            OverlapKind.Ambiguous,
            default,
            default,
            0m,
            0m,
            OverlapRecommendationAction.None,
            null,
            null,
            null,
            OverlapConfidence.Ambiguous,
            string.Empty,
            OverlapGpsSupport.None,
            "overlapBelowTolerance");

    public bool HasOverlap => OverlapHours >= OverlapIntelligence.MinimumOverlapHours;
}

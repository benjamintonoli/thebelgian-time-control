using System.Globalization;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

public static class DailyReviewDisplay
{
    public static string Evidence(DailyReviewEvidenceLevel level) => level switch
    {
        DailyReviewEvidenceLevel.Complete => "Bewijs volledig",
        DailyReviewEvidenceLevel.Partial => "Bewijs gedeeltelijk",
        _ => "Onvoldoende locatiegegevens",
    };

    public static string Status(DailyReviewWorkflowStatus status) => status switch
    {
        DailyReviewWorkflowStatus.Open => "Open",
        DailyReviewWorkflowStatus.ResolvedNoAction => "Geen probleem / verklaard",
        DailyReviewWorkflowStatus.PendingCorrection => "Administratieve correctie voorgesteld",
        DailyReviewWorkflowStatus.AwaitingExplanation => "Uitleg nodig",
        DailyReviewWorkflowStatus.EscalatedForManagementReview => "Manueel geëscaleerd",
        DailyReviewWorkflowStatus.NeedsReReview => "Gegevens gewijzigd — opnieuw controleren",
        DailyReviewWorkflowStatus.CorrectionExecuted => "Correctie uitgevoerd in Plenion",
        _ => status.ToString(),
    };

    public static string Reason(ReviewFeedbackReason reason) => reason switch
    {
        ReviewFeedbackReason.CorrectRegistration => "Correcte registratie",
        ReviewFeedbackReason.AdministrativeEntryError => "Administratieve invoerfout",
        ReviewFeedbackReason.AlternativeWorkLocation => "Andere geldige werklocatie",
        ReviewFeedbackReason.SharedVehicle => "Samen gereden / ander voertuig",
        ReviewFeedbackReason.WrongVehicleAssignment => "Verkeerde voertuigkoppeling",
        ReviewFeedbackReason.GpsIssue => "GPS niet representatief",
        ReviewFeedbackReason.LargeCampus => "Grote site of campus",
        ReviewFeedbackReason.ExplanationAccepted => "Verklaring aanvaard",
        ReviewFeedbackReason.UnexplainedMismatch => "Onverklaard tijdsverschil",
        _ => "Andere verklaring",
    };

    public static string Difference(DailyReviewBoundaryEvidence boundary)
    {
        if (boundary.GpsTime is null || boundary.SignedDifferenceMinutes is null)
        {
            return "Onvoldoende locatiegegevens voor deze vergelijking.";
        }

        var signed = boundary.SignedDifferenceMinutes.Value;
        var minutes = Math.Round(Math.Abs(signed), MidpointRounding.AwayFromZero);
        var duration = minutes < 1
            ? "minder dan 1 minuut"
            : minutes == 1
                ? "ongeveer 1 minuut"
                : string.Create(CultureInfo.InvariantCulture, $"ongeveer {minutes:0} minuten");
        if (boundary.Side == "Start")
        {
            if (signed > 0)
            {
                return $"De prestatie werd {duration} vóór de GPS-aankomst geregistreerd.";
            }

            if (signed < 0)
            {
                return $"Het voertuig was {duration} vóór de geregistreerde start aanwezig.";
            }

            return "De geregistreerde start en GPS-aankomst vallen samen.";
        }

        if (signed > 0)
        {
            return $"De geregistreerde prestatie liep {duration} door nadat het voertuig vertrok.";
        }

        if (signed < 0)
        {
            return $"Het voertuig vertrok {duration} na het geregistreerde einde.";
        }

        return "Het geregistreerde einde en GPS-vertrek vallen samen.";
    }

    public static string CompactDifference(double? minutes)
    {
        if (minutes is null)
        {
            return "—";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes.Value:+0.0;-0.0;0.0} min");
    }

    public static string BoundaryImpact(DailyReviewBoundaryEvidence boundary)
    {
        if (!boundary.IsReliable || boundary.SignedDifferenceMinutes is null)
        {
            return "Niet betrouwbaar te beoordelen";
        }

        var minutes = boundary.SignedDifferenceMinutes.Value;
        if (Math.Round(Math.Abs(minutes), MidpointRounding.AwayFromZero) < 1)
        {
            return "Nagenoeg gelijk — minder dan 1 minuut verschil";
        }

        return minutes > 0
            ? "In voordeel van de technieker — meer tijd geregistreerd dan de GPS-boundary"
            : "In nadeel van de technieker — minder tijd geregistreerd dan de GPS-boundary";
    }

    public static string BoundaryImpactClass(DailyReviewBoundaryEvidence boundary)
    {
        if (!boundary.IsReliable || boundary.SignedDifferenceMinutes is null ||
            Math.Round(Math.Abs(boundary.SignedDifferenceMinutes.Value),
                MidpointRounding.AwayFromZero) < 1)
        {
            return "impact-neutral";
        }

        return boundary.SignedDifferenceMinutes > 0 ? "impact-positive" : "impact-negative";
    }

    public static string DayInterpretation(DailyReviewCase reviewCase)
    {
        var reliable = new[] { reviewCase.First, reviewCase.Last }
            .Where(item => item.IsReliable && item.SignedDifferenceMinutes is not null)
            .Select(item => item.SignedDifferenceMinutes!.Value)
            .ToArray();
        if (reliable.Length == 0)
        {
            return "Er is onvoldoende betrouwbaar GPS-bewijs voor een daginterpretatie.";
        }

        var net = reliable.Sum();
        if (Math.Abs(net) < 1)
        {
            return "De betrouwbare start- en eindvergelijking zijn samen nagenoeg neutraal.";
        }

        var direction = net > 0 ? "meer" : "minder";
        return string.Create(CultureInfo.InvariantCulture,
            $"De betrouwbare boundaries tonen samen ongeveer {Math.Abs(net):0} minuten {direction} geregistreerde tijd dan de GPS-momenten. Dit is operationele context, geen automatische conclusie.");
    }

    public static string TripDistance(DailyReviewTrip trip)
    {
        if (trip.DistanceKilometres is null)
        {
            return "Afstand niet beschikbaar";
        }

        var prefix = trip.DistanceIsEstimated ? "ca. " : string.Empty;
        var suffix = trip.DistanceIsEstimated ? " km hemelsbreed" : " km";
        return string.Create(CultureInfo.InvariantCulture,
            $"{prefix}{trip.DistanceKilometres.Value:0.0}{suffix}");
    }

    public static string ApproximateTime(DateTimeOffset? value) => value is null
        ? "Onvoldoende gegevens"
        : value.Value.AddSeconds(30).ToString("HH:mm", CultureInfo.InvariantCulture);

    public static TimeOnly? ReliableGpsCorrectionTime(DailyReviewBoundaryEvidence boundary)
    {
        if (!boundary.IsReliable || boundary.GpsTime is null)
        {
            return null;
        }

        var rounded = boundary.GpsTime.Value.AddSeconds(30);
        return new TimeOnly(rounded.Hour, rounded.Minute);
    }

    public static bool CanCorrectBoundary(DailyReviewBoundaryEvidence boundary) =>
        boundary.IsReliable && boundary.PerformanceId > 0;

    public static bool IsDirectCorrectionActionable(DailyReviewCase reviewCase) =>
        CanCorrectBoundary(reviewCase.First) || CanCorrectBoundary(reviewCase.Last);

    public static bool IsMeaningfulTimeChange(DateTimeOffset original, TimeOnly? proposed) =>
        proposed is not null &&
        (proposed.Value.Hour != original.Hour || proposed.Value.Minute != original.Minute);

    public static string RegisteredTime(DateTimeOffset value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string ResolveReviewer(string? authenticatedUser, string defaultReviewer)
    {
        var reviewer = string.IsNullOrWhiteSpace(authenticatedUser)
            ? defaultReviewer
            : authenticatedUser;
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new InvalidOperationException("AdminReview:DefaultReviewer ontbreekt.");
        }

        return reviewer.Trim();
    }

    public static string? AdjacentCaseId(
        IReadOnlyList<DailyReviewCase> cases,
        string selectedCaseId,
        int direction)
    {
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        var index = -1;
        for (var current = 0; current < cases.Count; current++)
        {
            if (string.Equals(cases[current].CaseId, selectedCaseId, StringComparison.Ordinal))
            {
                index = current;
                break;
            }
        }

        var adjacent = index + direction;
        return index >= 0 && adjacent >= 0 && adjacent < cases.Count
            ? cases[adjacent].CaseId
            : null;
    }

    public static string? NextOpenCaseId(
        IReadOnlyList<DailyReviewCase> remainingOpenCases,
        int? previousIndex)
    {
        if (remainingOpenCases.Count == 0)
        {
            return null;
        }

        return remainingOpenCases[Math.Min(
            Math.Max(previousIndex ?? 0, 0),
            remainingOpenCases.Count - 1)].CaseId;
    }
}

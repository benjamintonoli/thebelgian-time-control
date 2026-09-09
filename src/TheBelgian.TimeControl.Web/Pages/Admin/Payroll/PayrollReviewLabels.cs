using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public static class PayrollReviewLabels
{
    public static string ReviewStatus(PayrollEmployeeReviewStatus status) =>
        status switch
        {
            PayrollEmployeeReviewStatus.Pending => "Te controleren",
            PayrollEmployeeReviewStatus.Accepted => "Goedgekeurd",
            PayrollEmployeeReviewStatus.NeedsFollowUp => "Opvolging nodig",
            PayrollEmployeeReviewStatus.ExcludedFromPayroll => "Uitgesloten",
            _ => status.ToString(),
        };

    public static string Eligibility(PayrollEligibilityStatus status) =>
        status switch
        {
            PayrollEligibilityStatus.NeedsDecision => "Nog te beslissen",
            PayrollEligibilityStatus.Included => "Included",
            PayrollEligibilityStatus.Excluded => "Excluded",
            _ => status.ToString(),
        };

    public static string MonthStatus(PayrollShadowMonthStatus status) =>
        status switch
        {
            PayrollShadowMonthStatus.WaitingForData => "Wacht op data",
            PayrollShadowMonthStatus.ReadyForReview => "Klaar voor review",
            PayrollShadowMonthStatus.InReview => "Review bezig",
            PayrollShadowMonthStatus.Finalized => "Afgesloten",
            _ => status.ToString(),
        };

    public static string FindingSeverity(PayrollFindingSeverity severity) =>
        severity switch
        {
            PayrollFindingSeverity.Info => "Info",
            PayrollFindingSeverity.Review => "Review",
            PayrollFindingSeverity.High => "High",
            _ => severity.ToString(),
        };

    public static string FindingStatus(PayrollFindingStatus status) =>
        PayrollReviewCategories.WorkflowStatusLabel(status);

    public static string Category(PayrollReviewCategory category) =>
        PayrollReviewCategories.DisplayName(category);

    public static string FindingType(PayrollFindingType type) =>
        type switch
        {
            PayrollFindingType.Project300WithoutPlanning => "Project 300 zonder planning",
            PayrollFindingType.Project200WithoutPlanning => "Project 200 zonder planning",
            PayrollFindingType.Project200ExceedsPlanning => "Project 200 langer dan planning",
            PayrollFindingType.Project100TrainingHours => "Toolbox/opleiding uren",
            PayrollFindingType.Project100TrainingInOvertime => "Toolbox veroorzaakt overuren",
            PayrollFindingType.Project100ExceedsPlannedDuration => "Toolbox langer dan gepland",
            PayrollFindingType.OverlappingPerformances => "Dubbele uren",
            PayrollFindingType.StandbyPhoneExceeds15Min => "Wachtdienst telefonisch langer dan 15 min",
            PayrollFindingType.StandbyStartMismatch => "Wachtdienst start wijkt af van GPS",
            PayrollFindingType.StandbyEndMismatch => "Wachtdienst einde wijkt af van GPS",
            PayrollFindingType.StandbyDurationMismatch => "Wachtdienst duur wijkt af van GPS",
            PayrollFindingType.StandbyPossibleWrongDossier => "Wachtdienst mogelijk op verkeerd dossier",
            PayrollFindingType.StandbyAmbiguousEvidence => "Wachtdienst GPS-bewijs onduidelijk",
            PayrollFindingType.StandbyNoGpsData => "Wachtdienst zonder GPS-bewijs",
            PayrollFindingType.MissingPlannedTechnicianPerformance => "Mogelijk ontbrekende prestatie",
            _ => type.ToString(),
        };

    public static string MissingTechnicianEvidence(string? classification) =>
        classification switch
        {
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps) => "Ondersteunt aanwezigheid (planning + collega + GPS)",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer) => "Planning + collega",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusGps) => "Planning + GPS",
            nameof(MissingTechnicianEvidenceClass.NoGpsData) => "Geen GPS",
            nameof(MissingTechnicianEvidenceClass.ContradictedByGps) => "Tegenstrijdig (GPS)",
            nameof(MissingTechnicianEvidenceClass.ContradictedByExistingPerformance) => "Tegenstrijdig (bestaande prestatie)",
            nameof(MissingTechnicianEvidenceClass.Ambiguous) => "Onduidelijk",
            _ => classification ?? "—",
        };

    public static string MissingTechnicianTravelMode(MissingTechnicianTravelMode mode) =>
        MissingTechnicianControl.TravelModeDutch(mode);

    public static string OverlapKind(OverlapKind kind) =>
        PayrollIntelligenceWorkbenchBuilder.OverlapKindLabelNl(kind);

    public static string SeverityBadgeClass(PayrollFindingSeverity severity) =>
        severity switch
        {
            PayrollFindingSeverity.High => "text-bg-danger",
            PayrollFindingSeverity.Review => "text-bg-warning",
            _ => "text-bg-secondary",
        };

    public static string FindingStatusBadgeClass(PayrollFindingStatus status) =>
        status switch
        {
            PayrollFindingStatus.Open => "text-bg-secondary",
            PayrollFindingStatus.NeedsFollowUp => "text-bg-warning",
            PayrollFindingStatus.Reviewed => "text-bg-success",
            PayrollFindingStatus.Resolved => "text-bg-success",
            PayrollFindingStatus.Dismissed => "text-bg-light text-dark",
            _ => "text-bg-secondary",
        };
}

using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public enum PayrollReviewCategory
{
    All = 0,
    Project300 = 1,
    Project200 = 2,
    Project100 = 3,
    Standby = 4,
    Overlap = 5,
    MissingPerformance = 6,
    Other = 7,
}

public enum PayrollReviewCaseActionability
{
    None = 0,
    NeedsControl = 1,
    ReadyProposal = 2,
    Blocked = 3,
}

/// <summary>
/// Presentation/workflow grouping of related payroll findings for the admin queue.
/// Does not replace persisted PayrollFindingRecords.
/// </summary>
public sealed record PayrollReviewCase(
    string CaseKey,
    PayrollReviewCategory Category,
    string ResourceId,
    string? DisplayName,
    DateOnly Date,
    PayrollFindingSeverity Severity,
    PayrollFindingStatus WorkflowStatus,
    string ProblemLabel,
    string? BonNr,
    string? ProjectId,
    string? BookedSummary,
    string? EvidenceSummary,
    string? ProposalSummary,
    PayrollReviewCaseActionability Actionability,
    string ActionabilityLabel,
    IReadOnlyList<int> FindingIds,
    IReadOnlyList<string> FindingKeys,
    IReadOnlyList<PayrollFindingType> FindingTypes,
    long? PrimaryPerformanceId,
    Guid? RelatedActionId,
    string? HybridScenarioNote,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewComment);

public sealed record PayrollReviewQueueFilter(
    PayrollReviewCategory Category = PayrollReviewCategory.All,
    string? Search = null,
    PayrollFindingStatus? WorkflowStatus = null,
    PayrollFindingSeverity? Severity = null,
    PayrollReviewCaseActionability? Actionability = null,
    string Sort = "default");

public sealed record PayrollReviewQueueSummary(
    int IncludedEmployees,
    int EmployeesWithFindings,
    int EmployeesWithoutFindings,
    int ReviewCasesTotal,
    int Open,
    int NeedsFollowUp,
    int Reviewed,
    int Resolved,
    int Dismissed,
    int ReadyActions,
    int BlockedActions,
    IReadOnlyDictionary<PayrollReviewCategory, int> ByCategory);

public sealed record PayrollReviewQueuePage(
    int Year,
    int Month,
    PayrollReviewQueueSummary Summary,
    IReadOnlyList<PayrollReviewCase> Cases,
    IReadOnlyList<PayrollShadowEmployeeResult> EmployeesAlphabetical,
    IReadOnlyList<(string ResourceId, string? DisplayName)> EmployeesWithOpenIssues,
    IReadOnlyList<(string ResourceId, string? DisplayName)> EmployeesWithoutOpenIssues);

public static class PayrollReviewCategories
{
    public static PayrollReviewCategory Map(PayrollFindingType type) => type switch
    {
        PayrollFindingType.Project300WithoutPlanning => PayrollReviewCategory.Project300,
        PayrollFindingType.Project200WithoutPlanning or PayrollFindingType.Project200ExceedsPlanning
            => PayrollReviewCategory.Project200,
        PayrollFindingType.Project100TrainingHours
            or PayrollFindingType.Project100TrainingInOvertime
            or PayrollFindingType.Project100ExceedsPlannedDuration
            => PayrollReviewCategory.Project100,
        PayrollFindingType.OverlappingPerformances => PayrollReviewCategory.Overlap,
        PayrollFindingType.MissingPlannedTechnicianPerformance => PayrollReviewCategory.MissingPerformance,
        PayrollFindingType.StandbyPhoneExceeds15Min
            or PayrollFindingType.StandbyStartMismatch
            or PayrollFindingType.StandbyEndMismatch
            or PayrollFindingType.StandbyDurationMismatch
            or PayrollFindingType.StandbyPossibleWrongDossier
            or PayrollFindingType.StandbyAmbiguousEvidence
            or PayrollFindingType.StandbyNoGpsData
            => PayrollReviewCategory.Standby,
        _ => PayrollReviewCategory.Other,
    };

    public static string DisplayName(PayrollReviewCategory category) => category switch
    {
        PayrollReviewCategory.All => "Alles",
        PayrollReviewCategory.Project300 => "300 zonder planning",
        PayrollReviewCategory.Project200 => "200 controle",
        PayrollReviewCategory.Project100 => "100 toolbox / opleiding",
        PayrollReviewCategory.Standby => "Wachtdienst",
        PayrollReviewCategory.Overlap => "Dubbele uren",
        PayrollReviewCategory.MissingPerformance => "Ontbrekende prestatie",
        PayrollReviewCategory.Other => "Overig",
        _ => category.ToString(),
    };

    public static string ProblemLabel(IReadOnlyList<PayrollFindingRecord> findings)
    {
        if (findings.Count == 0)
        {
            return "Onbekend";
        }

        var types = findings.Select(item => item.FindingType).Distinct().OrderBy(item => item).ToList();
        if (types.All(IsStandbyTimeMismatch) && types.Count > 1)
        {
            return "Start/einde/duur wijkt af van GPS";
        }

        return types[0] switch
        {
            PayrollFindingType.Project300WithoutPlanning => "300 zonder planning",
            PayrollFindingType.Project200WithoutPlanning => "200 zonder planning",
            PayrollFindingType.Project200ExceedsPlanning => "200 meer geboekt dan gepland",
            PayrollFindingType.Project100TrainingHours => "Toolbox / opleiding",
            PayrollFindingType.Project100TrainingInOvertime => "Opleiding veroorzaakt mogelijk overuren",
            PayrollFindingType.Project100ExceedsPlannedDuration => "Meer geboekt dan gepland",
            PayrollFindingType.OverlappingPerformances => "Dubbele / overlappende uren",
            PayrollFindingType.MissingPlannedTechnicianPerformance => "Mogelijk ontbrekende prestatie",
            PayrollFindingType.StandbyPhoneExceeds15Min => "Telefonisch > 15 min",
            PayrollFindingType.StandbyStartMismatch => "Start wijkt af van GPS",
            PayrollFindingType.StandbyEndMismatch => "Einde wijkt af van GPS",
            PayrollFindingType.StandbyDurationMismatch => "Duur wijkt af van GPS",
            PayrollFindingType.StandbyPossibleWrongDossier => "Mogelijk verkeerd dossier",
            PayrollFindingType.StandbyAmbiguousEvidence => "Onvoldoende / ambigu GPS",
            PayrollFindingType.StandbyNoGpsData => "Onvoldoende GPS",
            _ => findings[0].Title,
        };
    }

    public static string WorkflowStatusLabel(PayrollFindingStatus status) => status switch
    {
        PayrollFindingStatus.Open => "Te controleren",
        PayrollFindingStatus.NeedsFollowUp => "Opvolging nodig",
        PayrollFindingStatus.Reviewed => "Gecontroleerd",
        PayrollFindingStatus.Resolved => "Opgelost",
        PayrollFindingStatus.Dismissed => "Niet van toepassing",
        _ => status.ToString(),
    };

    private static bool IsStandbyTimeMismatch(PayrollFindingType type) =>
        type is PayrollFindingType.StandbyStartMismatch
            or PayrollFindingType.StandbyEndMismatch
            or PayrollFindingType.StandbyDurationMismatch;
}

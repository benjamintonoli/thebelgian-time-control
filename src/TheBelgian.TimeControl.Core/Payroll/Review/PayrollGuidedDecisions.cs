using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public static class PayrollGuidedDecisionCodes
{
    public const string P300WorkValid = "P300_WORK_VALID";
    public const string P300PlanningMissing = "P300_PLANNING_MISSING";
    public const string P300HoursWrong = "P300_HOURS_WRONG";
    public const string P300Uncertain = "P300_UNCERTAIN";

    public const string P200Valid = "P200_VALID";
    public const string P200PlanningMissing = "P200_PLANNING_MISSING";
    public const string P200HoursReview = "P200_HOURS_REVIEW";
    public const string P200Uncertain = "P200_UNCERTAIN";

    public const string P100Valid = "P100_VALID";
    public const string P100HoursWrong = "P100_HOURS_WRONG";
    public const string P100PlanningMissing = "P100_PLANNING_MISSING";
    public const string P100DurationWrong = "P100_DURATION_WRONG"; // legacy alias kept for older decisions
    public const string P100NotTraining = "P100_NOT_TRAINING";
    public const string P100Uncertain = "P100_UNCERTAIN";

    public const string StandbyBookingCorrect = "STANDBY_BOOKING_CORRECT";
    public const string StandbyPhysicalOnly = "STANDBY_PHYSICAL_ONLY";
    public const string StandbyPhoneThenPhysical = "STANDBY_PHONE_THEN_PHYSICAL";
    public const string StandbyPhoneOnly = "STANDBY_PHONE_ONLY";
    public const string StandbyHoursWrong = "STANDBY_HOURS_WRONG";
    public const string StandbyWrongDossier = "STANDBY_WRONG_DOSSIER";
    public const string StandbyUncertain = "STANDBY_UNCERTAIN";

    public const string MissingTechConfirmed = "MISSING_TECH_CONFIRMED";
    public const string MissingTechPlanningWrong = "MISSING_TECH_PLANNING_WRONG";
    public const string MissingTechUncertain = "MISSING_TECH_UNCERTAIN";

    public const string OverlapAWrong = "OVERLAP_A_WRONG";
    public const string OverlapBWrong = "OVERLAP_B_WRONG";
    public const string OverlapBothValid = "OVERLAP_BOTH_VALID";
    public const string OverlapUncertain = "OVERLAP_UNCERTAIN";
}

public static class PayrollGuidedDecisions
{
    public static string BusinessQuestion(PayrollReviewCategory category, bool hybridStandby = false) => category switch
    {
        PayrollReviewCategory.Project300 => "Waarom werden deze uren op 300 geboekt zonder reservatie?",
        PayrollReviewCategory.Project200 => "Kloppen deze interne uren?",
        PayrollReviewCategory.Project100 => "Klopt deze toolbox/opleiding en de duur?",
        PayrollReviewCategory.Standby => "Hoe verliep deze wachtdienst?",
        PayrollReviewCategory.MissingPerformance => "Heeft deze technieker hier effectief gewerkt?",
        PayrollReviewCategory.Overlap => "Zijn deze uren dubbel geboekt?",
        _ => "Wat is de juiste beoordeling voor deze controle?",
    };

    public static IReadOnlyList<PayrollGuidedChoice> ChoicesFor(PayrollReviewCategory category) => category switch
    {
        PayrollReviewCategory.Project300 =>
        [
            Choice(PayrollGuidedDecisionCodes.P300WorkValid, "Werk was terecht", PayrollFindingStatus.Reviewed, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P300PlanningMissing, "Planning ontbreekt", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            // Comment optional: opens inline adjust/delete actions; admin note stays separate.
            Choice(PayrollGuidedDecisionCodes.P300HoursWrong, "Uren zijn fout", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P300Uncertain, "Onzeker", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
        ],
        PayrollReviewCategory.Project200 =>
        [
            Choice(PayrollGuidedDecisionCodes.P200Valid, "Werk was terecht", PayrollFindingStatus.Reviewed, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P200PlanningMissing, "Planning ontbreekt", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            // Comment optional: opens inline adjust/delete actions; admin note stays separate.
            Choice(PayrollGuidedDecisionCodes.P200HoursReview, "Uren zijn fout", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P200Uncertain, "Onzeker", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
        ],
        PayrollReviewCategory.Project100 =>
        [
            Choice(PayrollGuidedDecisionCodes.P100Valid, "Boeking is correct", PayrollFindingStatus.Reviewed, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P100HoursWrong, "Uren zijn fout", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P100PlanningMissing, "Planning ontbreekt", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.P100Uncertain, "Onzeker", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
        ],
        PayrollReviewCategory.Standby =>
        [
            Choice(PayrollGuidedDecisionCodes.StandbyBookingCorrect, "Boeking is correct", PayrollFindingStatus.Reviewed, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.StandbyPhoneOnly, "Telefoon enkel", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.StandbyPhysicalOnly, "Fysieke interventie", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.StandbyPhoneThenPhysical, "Telefoon + interventie", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.StandbyHoursWrong, "Uren zijn fout", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.StandbyWrongDossier, "Dossier nakijken", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
            Choice(PayrollGuidedDecisionCodes.StandbyUncertain, "Onzeker", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
        ],
        PayrollReviewCategory.MissingPerformance =>
        [
            Choice(PayrollGuidedDecisionCodes.MissingTechConfirmed, "Prestatie aanmaken", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.MissingTechPlanningWrong, "Niet aanmaken", PayrollFindingStatus.Reviewed, requiresComment: false),
            Choice(PayrollGuidedDecisionCodes.MissingTechUncertain, "Onzeker", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
        ],
        PayrollReviewCategory.Overlap =>
        [
            Choice(PayrollGuidedDecisionCodes.OverlapAWrong, "Prestatie A fout", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
            Choice(PayrollGuidedDecisionCodes.OverlapBWrong, "Prestatie B fout", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
            Choice(PayrollGuidedDecisionCodes.OverlapBothValid, "Beide correct", PayrollFindingStatus.Reviewed, requiresComment: true),
            Choice(PayrollGuidedDecisionCodes.OverlapUncertain, "Onzeker", PayrollFindingStatus.NeedsFollowUp, requiresComment: true),
        ],
        _ =>
        [
            Choice("OTHER_REVIEWED", "Afgehandeld", PayrollFindingStatus.Reviewed, requiresComment: true),
            Choice("OTHER_FOLLOWUP", "Opvolging nodig", PayrollFindingStatus.NeedsFollowUp, requiresComment: false),
            Choice("OTHER_DISMISSED", "Niet van toepassing", PayrollFindingStatus.Dismissed, requiresComment: true),
        ],
    };

    public static PayrollGuidedChoice Resolve(string decisionCode, PayrollReviewCategory category)
    {
        var match = ChoicesFor(category)
            .FirstOrDefault(item => string.Equals(item.DecisionCode, decisionCode, StringComparison.Ordinal));
        if (match is null)
        {
            throw new InvalidOperationException($"Onbekende beslissing: {decisionCode}");
        }

        return match;
    }

    public static string AdminStatusLabel(PayrollFindingStatus status) => status switch
    {
        PayrollFindingStatus.Open => "Te beoordelen",
        PayrollFindingStatus.NeedsFollowUp => "Opvolging nodig",
        PayrollFindingStatus.Reviewed or PayrollFindingStatus.Resolved => "Afgehandeld",
        PayrollFindingStatus.Dismissed => "Niet van toepassing",
        _ => status.ToString(),
    };

    public static string ActionabilityHint(PayrollReviewCaseActionability actionability, string? hybridNote) =>
        actionability switch
        {
            PayrollReviewCaseActionability.ReadyProposal => "Voorstel beschikbaar (geen auto-uitvoering)",
            PayrollReviewCaseActionability.Blocked when !string.IsNullOrWhiteSpace(hybridNote) =>
                "Correctie vereist verdere controle",
            PayrollReviewCaseActionability.Blocked => "Automatische correctie niet beschikbaar",
            PayrollReviewCaseActionability.NeedsControl => "Controle nodig",
            _ => string.Empty,
        };

    private static PayrollGuidedChoice Choice(
        string code,
        string label,
        PayrollFindingStatus status,
        bool requiresComment) =>
        new(code, label, status, requiresComment);
}

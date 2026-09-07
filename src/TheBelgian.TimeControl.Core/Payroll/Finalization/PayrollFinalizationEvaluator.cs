using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Payroll.Finalization;

public static class PayrollFinalizationBlockerCodes
{
    public const string OpenReviewCases = "OPEN_REVIEW_CASES";
    public const string FollowUpReviewCases = "FOLLOWUP_REVIEW_CASES";
    public const string EmployeeNeedsFollowUp = "EMPLOYEE_NEEDS_FOLLOWUP";
    public const string EligibilityNeedsDecision = "ELIGIBILITY_NEEDS_DECISION";
    public const string MissingAcertaId = "MISSING_ACERTA_ID";
    public const string NoIncludedEmployees = "NO_INCLUDED_EMPLOYEES";
    public const string CalculationIncomplete = "CALCULATION_INCOMPLETE";
    public const string ConfigInvalid = "CONFIG_INVALID";
    public const string AlreadyFinalized = "ALREADY_FINALIZED";
    public const string InvalidMonthStatus = "INVALID_MONTH_STATUS";
}

/// <summary>
/// Exception-based month finalization rules (V2).
/// Employee ReviewStatus=Pending is informational only — never a blocker.
/// </summary>
public static class PayrollFinalizationEvaluator
{
    public static PayrollMonthFinalizationBlockers Evaluate(
        PayrollShadowMonth shadowMonth,
        IReadOnlyList<PayrollShadowEmployeeResult> employees,
        IReadOnlyList<PayrollReviewCase> reviewCases)
    {
        var included = employees
            .Where(item => item.EligibilityStatus == PayrollEligibilityStatus.Included)
            .ToList();
        var excluded = employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Excluded);
        var pending = included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.Pending);
        var employeeFollowUp = included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.NeedsFollowUp);
        var needsDecision = employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision);
        var missingAcerta = included.Count(item => item.AcertaIdentityStatus == AcertaIdentityStatus.Missing);
        var incompleteCalc = included.Count(IsIncompleteCalculation);

        var openCases = reviewCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Open);
        var followUpCases = reviewCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp);
        var reviewed = reviewCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Reviewed);
        var resolved = reviewCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Resolved);
        var dismissed = reviewCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Dismissed);

        var blockers = new List<PayrollFinalizationBlocker>();

        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.AlreadyFinalized,
                1,
                "Maand is al afgesloten."));
        }
        else if (shadowMonth.Status is not (PayrollShadowMonthStatus.ReadyForReview or PayrollShadowMonthStatus.InReview))
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.InvalidMonthStatus,
                1,
                "Shadow-maand is niet klaar om af te sluiten."));
        }

        if (included.Count == 0)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.NoIncludedEmployees,
                0,
                "Geen Included medewerkers."));
        }

        if (needsDecision > 0)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.EligibilityNeedsDecision,
                needsDecision,
                $"{needsDecision} medewerker(s) met NeedsDecision."));
        }

        if (missingAcerta > 0)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.MissingAcertaId,
                missingAcerta,
                $"{missingAcerta} Included medewerker(s) zonder Acerta-ID."));
        }

        if (incompleteCalc > 0)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.CalculationIncomplete,
                incompleteCalc,
                $"{incompleteCalc} Included medewerker(s) met incomplete berekening."));
        }

        if (string.IsNullOrWhiteSpace(shadowMonth.ConfigurationSnapshotJson)
            || string.IsNullOrWhiteSpace(shadowMonth.CalculationVersion))
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.ConfigInvalid,
                1,
                "Configuratie- of berekeningsversie ontbreekt in de snapshot."));
        }

        if (openCases > 0)
        {
            var detail = FormatUnresolvedCategories(reviewCases, PayrollFindingStatus.Open);
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.OpenReviewCases,
                openCases,
                detail is null
                    ? $"{openCases} openstaande payrollcontroles."
                    : $"{openCases} openstaande payrollcontroles. Nog af te handelen: {detail}"));
        }

        if (followUpCases > 0)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.FollowUpReviewCases,
                followUpCases,
                $"{followUpCases} payrollcontrole(s) met opvolging nodig."));
        }

        if (employeeFollowUp > 0)
        {
            blockers.Add(new(
                PayrollFinalizationBlockerCodes.EmployeeNeedsFollowUp,
                employeeFollowUp,
                $"{employeeFollowUp} medewerker(s) expliciet gemarkeerd als opvolging nodig."));
        }

        var financials = SumFinancials(included);
        var canFinalize = blockers.Count == 0
            && shadowMonth.Status is PayrollShadowMonthStatus.ReadyForReview or PayrollShadowMonthStatus.InReview;

        return new PayrollMonthFinalizationBlockers(
            canFinalize,
            pending,
            employeeFollowUp,
            needsDecision,
            missingAcerta,
            included.Count,
            excluded,
            openCases,
            followUpCases,
            reviewed,
            resolved,
            dismissed,
            reviewCases.Count,
            incompleteCalc,
            blockers,
            financials,
            shadowMonth.CalculationVersion ?? string.Empty,
            !string.IsNullOrWhiteSpace(shadowMonth.ConfigurationSnapshotJson),
            blockers.Select(item => item.FriendlyMessage).ToArray());
    }

    public static string BuildAuditSnapshotJson(
        PayrollShadowMonth month,
        PayrollMonthFinalizationBlockers blockers,
        string? comment)
    {
        // Structured audit payload — no national-register / Acerta identity values.
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = "payroll-month-finalization-v2",
            ["year"] = month.Year,
            ["month"] = month.Month,
            ["comment"] = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            ["includedCount"] = blockers.IncludedCount,
            ["excludedCount"] = blockers.ExcludedCount,
            ["reviewCasesTotal"] = blockers.ReviewCasesTotal,
            ["reviewOpen"] = blockers.OpenReviewCases,
            ["reviewFollowUp"] = blockers.FollowUpReviewCases,
            ["reviewReviewed"] = blockers.ReviewedReviewCases,
            ["reviewResolved"] = blockers.ResolvedReviewCases,
            ["reviewDismissed"] = blockers.DismissedReviewCases,
            ["needsDecision"] = blockers.NeedsDecision,
            ["missingAcerta"] = blockers.MissingAcertaIncluded,
            ["employeeNeedsFollowUp"] = blockers.NeedsFollowUpIncluded,
            ["employeePendingInformational"] = blockers.PendingIncluded,
            ["calculationVersion"] = blockers.CalculationVersion,
            ["hasConfigurationSnapshot"] = blockers.HasConfigurationSnapshot,
            ["financial"] = new Dictionary<string, decimal>
            {
                ["overtime150Units"] = blockers.FinancialSummary.TotalOvertime150Units,
                ["standby200Hours"] = blockers.FinancialSummary.TotalStandby200Hours,
                ["cityAllowanceAmount"] = blockers.FinancialSummary.TotalCityAllowanceAmount,
                ["kmAmount"] = blockers.FinancialSummary.TotalKmAmount,
                ["code414Amount"] = blockers.FinancialSummary.TotalCode414Amount,
            },
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static bool IsIncompleteCalculation(PayrollShadowEmployeeResult employee) =>
        employee.OrdinaryStatus != PayrollMonthCalculationStatus.Calculated
        || employee.StandbyStatus != PayrollMonthCalculationStatus.Calculated
        || employee.CityStatus != PayrollMonthCalculationStatus.Calculated
        || employee.KmStatus != PayrollMonthCalculationStatus.Calculated
        || employee.Code414Status != PayrollMonthCalculationStatus.Calculated;

    private static PayrollMonthFinancialSummary SumFinancials(IReadOnlyList<PayrollShadowEmployeeResult> included) =>
        new(
            included.Sum(item => item.Code135At150Units ?? 0m),
            included.Sum(item => item.StandbyRoundedHours ?? 0m),
            included.Sum(item => item.CityAllowanceAmount ?? 0m),
            included.Sum(item => item.KmAmount ?? 0m),
            included.Sum(item => item.Code414Amount ?? 0m));

    private static string? FormatUnresolvedCategories(
        IReadOnlyList<PayrollReviewCase> reviewCases,
        PayrollFindingStatus status)
    {
        var parts = reviewCases
            .Where(item => item.WorkflowStatus == status)
            .GroupBy(item => item.Category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => $"{group.Count()} {PayrollReviewCategories.DisplayName(group.Key)}")
            .ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }
}

using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Classifies KALENDER task types for special-project planning evidence.
/// Absence/standby never support project 200/300 work reservations.
/// </summary>
public static class PayrollPlanningClassifier
{
    private static readonly HashSet<int> AbsenceTaskTypes = [3, 5, 8, 10, 39];
    private static readonly HashSet<int> StandbyTaskTypes = [36];
    private static readonly HashSet<int> ExplicitWorkOrTrainingTypes = [1, 2, 7, 9, 13, 15, 19, 21, 22, 23, 25, 26, 27, 29, 30, 31, 32, 33, 34, 37, 41, 42, 43, 44, 45, 46, 48];

    public static bool IsAbsenceOrStandby(int taskTypeId) =>
        AbsenceTaskTypes.Contains(taskTypeId) || StandbyTaskTypes.Contains(taskTypeId);

    public static bool IsTrainingTaskType(int taskTypeId) => taskTypeId == 9;

    public static PayrollPlanningClassification Classify(int taskTypeId)
    {
        if (AbsenceTaskTypes.Contains(taskTypeId))
        {
            return PayrollPlanningClassification.Absence;
        }

        if (StandbyTaskTypes.Contains(taskTypeId))
        {
            return PayrollPlanningClassification.Standby;
        }

        if (ExplicitWorkOrTrainingTypes.Contains(taskTypeId))
        {
            return PayrollPlanningClassification.WorkReservation;
        }

        return PayrollPlanningClassification.Ambiguous;
    }

    public static bool CanSupportSpecialProjectWork(PayrollPlanningReservation reservation) =>
        reservation.Classification is PayrollPlanningClassification.WorkReservation
            or PayrollPlanningClassification.Ambiguous;

    public static bool IsSpecialProjectNumber(int? projectNumber) =>
        projectNumber is 100 or 200 or 300;

    public static bool LooksLikeTrainingText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("toolbox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("opleiding", StringComparison.OrdinalIgnoreCase)
            || text.Contains("training", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vorming", StringComparison.OrdinalIgnoreCase)
            || text.Contains("toolboxmeeting", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrainingPerformance(
        NormalizedPerformanceEntry performance,
        IReadOnlyList<PayrollPlanningReservation> matchingPlanning)
    {
        if (performance.ProjectNumber != 100)
        {
            return false;
        }

        if (performance.HfdTaakId == 21)
        {
            return true;
        }

        if (LooksLikeTrainingText(performance.Description) || LooksLikeTrainingText(performance.Memo))
        {
            return true;
        }

        return matchingPlanning.Any(item =>
            IsTrainingTaskType(item.TaskTypeId) || LooksLikeTrainingText(item.Subject));
    }
}

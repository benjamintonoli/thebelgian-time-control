namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Power BI CJ / CJ_FirstDay / CJ_LastDay mapped to an explicit EvaluationDate
/// (replacement for TODAY()). Calculation upper bound is EvaluationDate, not LastDay.
/// KM measures also require PayrollPeriodSnapshot intersection — see LegacyKmEffectiveDateRange.
/// </summary>
public sealed record LegacyCurrentYearWindow(
    int Year,
    DateOnly FirstDay,
    DateOnly LastDay,
    DateOnly EvaluationDate)
{
    public static LegacyCurrentYearWindow FromEvaluationDate(DateOnly evaluationDate) =>
        new(
            evaluationDate.Year,
            new DateOnly(evaluationDate.Year, 1, 1),
            new DateOnly(evaluationDate.Year, 12, 31),
            evaluationDate);

    public static LegacyCurrentYearWindow FromPeriod(PayrollPeriodSnapshot period) =>
        FromEvaluationDate(period.EvaluationDate);

    /// <summary>
    /// Inclusive current-year calculation window: FirstDay through EvaluationDate.
    /// </summary>
    public bool IsInCalculationWindow(DateOnly date) =>
        date >= FirstDay && date <= EvaluationDate;
}

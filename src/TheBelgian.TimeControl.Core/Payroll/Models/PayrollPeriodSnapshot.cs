namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Explicit payroll period context. Pure calculators must not read the system clock.
/// </summary>
public sealed record PayrollPeriodSnapshot(
    int Year,
    int Month,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly EvaluationDate)
{
    public static PayrollPeriodSnapshot ForMonth(int year, int month, DateOnly evaluationDate)
    {
        var periodStart = new DateOnly(year, month, 1);
        var periodEnd = month == 12
            ? new DateOnly(year + 1, 1, 1).AddDays(-1)
            : new DateOnly(year, month + 1, 1).AddDays(-1);
        return new PayrollPeriodSnapshot(year, month, periodStart, periodEnd, evaluationDate);
    }

    /// <summary>
    /// Power BI EffectiveEnd = MIN(MonthEnd, EvaluationDate).
    /// </summary>
    public DateOnly EffectiveEnd =>
        EvaluationDate < PeriodStart
            ? PeriodStart.AddDays(-1)
            : EvaluationDate > PeriodEnd
                ? PeriodEnd
                : EvaluationDate;
}

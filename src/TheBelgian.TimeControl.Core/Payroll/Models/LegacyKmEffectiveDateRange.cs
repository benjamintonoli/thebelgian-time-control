namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Effective KM / Extra75 date range =
/// intersection(PayrollPeriodSnapshot visible dates, CJ_FirstDay..EvaluationDate).
/// Preserves Power BI report/payroll filter context; CJ is a date window, not ALL().
/// </summary>
public sealed record LegacyKmEffectiveDateRange(DateOnly Start, DateOnly End)
{
    public static LegacyKmEffectiveDateRange? Intersect(
        PayrollPeriodSnapshot period,
        LegacyCurrentYearWindow window)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(window);

        // Visible payroll/report dates: PeriodStart .. MIN(PeriodEnd, EvaluationDate)
        var visibleStart = period.PeriodStart;
        var visibleEnd = period.EffectiveEnd;
        if (visibleStart > visibleEnd)
        {
            return null;
        }

        var effectiveStart = visibleStart > window.FirstDay ? visibleStart : window.FirstDay;
        var effectiveEnd = visibleEnd < window.EvaluationDate ? visibleEnd : window.EvaluationDate;
        if (effectiveStart > effectiveEnd)
        {
            return null;
        }

        return new LegacyKmEffectiveDateRange(effectiveStart, effectiveEnd);
    }

    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

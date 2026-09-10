namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Benjamin daily pause rule for Create/Adjust proposals.
/// TOTAL daily work &gt; 4:00 ⇒ minimum 30 minutes pause across the day.
/// Existing registered pause counts; never force exactly 30; never 30 per row.
/// </summary>
public static class PayrollDailyPauseRules
{
    public static readonly TimeSpan FourHourThreshold = TimeSpan.FromHours(4);
    public static readonly TimeSpan MandatoryMinimumPause = TimeSpan.FromMinutes(30);

    public sealed record DayPauseInput(
        TimeSpan GrossDuration,
        TimeSpan RegisteredPause);

    public sealed record DayPausePlan(
        TimeSpan TotalGrossWork,
        TimeSpan ExistingEffectivePause,
        TimeSpan RequiredMinimumPause,
        TimeSpan PauseDeficit,
        TimeSpan PauseToAssignOnTargetRow,
        string ExplanationNl);

    /// <param name="otherSameDayRows">
    /// Other performances on the same employee/date (exclude the target row when adjusting).
    /// </param>
    /// <param name="proposedGrossDuration">Gross VAN→TOT of the create/adjust target.</param>
    /// <param name="targetRowCurrentPause">
    /// Current PAUZE on the target row (0 for create). Preserved when already ≥ deficit.
    /// </param>
    public static DayPausePlan PlanForCreateOrAdjust(
        IReadOnlyList<DayPauseInput> otherSameDayRows,
        TimeSpan proposedGrossDuration,
        TimeSpan targetRowCurrentPause = default)
    {
        var otherGross = TimeSpan.Zero;
        var otherPause = TimeSpan.Zero;
        foreach (var row in otherSameDayRows)
        {
            if (row.GrossDuration > TimeSpan.Zero)
                otherGross += row.GrossDuration;
            if (row.RegisteredPause > TimeSpan.Zero)
                otherPause += row.RegisteredPause;
        }

        var totalGross = otherGross + proposedGrossDuration;
        var effectiveExisting = otherPause + (targetRowCurrentPause > TimeSpan.Zero
            ? targetRowCurrentPause
            : TimeSpan.Zero);
        var required = totalGross > FourHourThreshold ? MandatoryMinimumPause : TimeSpan.Zero;
        var deficit = required > effectiveExisting ? required - effectiveExisting : TimeSpan.Zero;
        var minOnTarget = required > otherPause ? required - otherPause : TimeSpan.Zero;
        var assign = targetRowCurrentPause > minOnTarget ? targetRowCurrentPause : minOnTarget;

        string explanation;
        if (required == TimeSpan.Zero)
        {
            explanation = "Dag ≤4 uur: geen verplichte 30 min pauze.";
        }
        else if (deficit == TimeSpan.Zero && assign == TimeSpan.Zero)
        {
            explanation = "30 min pauze is reeds aanwezig op een andere prestatie van deze dag.";
        }
        else if (deficit == TimeSpan.Zero && assign > MandatoryMinimumPause)
        {
            explanation = $"Dag >4 uur: bestaande pauze {Format(assign)} behouden (≥30 min).";
        }
        else if (otherPause > TimeSpan.Zero && assign > TimeSpan.Zero)
        {
            explanation =
                $"Dag >4 uur: minimum 30 min pauze; {Format(assign)} op deze rij (bestaand elders {Format(otherPause)}).";
        }
        else if (assign >= MandatoryMinimumPause)
        {
            explanation = "Dag >4 uur: minimum 30 min pauze toegepast.";
        }
        else
        {
            explanation = $"Dag >4 uur: pauze {Format(assign)} toegepast.";
        }

        return new DayPausePlan(
            totalGross,
            effectiveExisting,
            required,
            deficit,
            assign,
            explanation);
    }

    /// <summary>Native Plenion: ATL = (TOT − VAN) − PAUZE.</summary>
    public static decimal DeriveNetAtl(TimeSpan van, TimeSpan tot, TimeSpan pause)
    {
        var hours = (decimal)(tot - van).TotalHours - (decimal)pause.TotalHours;
        return Math.Round(hours, 4, MidpointRounding.AwayFromZero);
    }

    public static string Format(TimeSpan value)
    {
        var totalMinutes = (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);
        var h = Math.DivRem(Math.Abs(totalMinutes), 60, out var m);
        return $"{h}u{m:00}";
    }
}

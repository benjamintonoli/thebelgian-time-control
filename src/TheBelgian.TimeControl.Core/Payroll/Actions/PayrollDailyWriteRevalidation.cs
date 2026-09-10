namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Execution-time revalidation of daily pause + write-safety overlap.
/// TimeControl owns the payroll business rule; Core only persists explicit VAN/TOT/PAUZE.
/// </summary>
public static class PayrollDailyWriteRevalidation
{
    public sealed record SameDayRow(
        long PerformanceId,
        TimeSpan Start,
        TimeSpan End,
        TimeSpan RegisteredPause);

    public sealed record CreateCheck(
        TimeSpan ProposedStart,
        TimeSpan ProposedEnd,
        TimeSpan ProposedPause);

    public sealed record AdjustCheck(
        long PerformanceId,
        TimeSpan ProposedStart,
        TimeSpan ProposedEnd,
        /// <summary>Null = VAN/TOT-only adjust; keep live pause on the target row.</summary>
        TimeSpan? ProposedPause,
        TimeSpan ExpectedCurrentStart,
        TimeSpan ExpectedCurrentEnd,
        TimeSpan? ExpectedCurrentPause);

    public sealed record Result(bool IsSafe, string? StaleReasonNl)
    {
        public static Result Safe() => new(true, null);
        public static Result Stale(string reason) => new(false, reason);
    }

    public static Result ValidateCreate(
        IReadOnlyList<SameDayRow> sameDayRows,
        CreateCheck proposal)
    {
        if (proposal.ProposedEnd <= proposal.ProposedStart)
            return Result.Stale("Voorstel heeft geen positieve duur; nieuw voorstel vereist.");

        var otherIntervals = sameDayRows
            .Select(row => new PayrollWriteOverlapSafety.Interval(row.Start, row.End, row.PerformanceId))
            .ToList();
        var overlap = PayrollWriteOverlapSafety.EvaluateCreateOrAdjust(
            proposal.ProposedStart,
            proposal.ProposedEnd,
            otherIntervals);
        if (overlap.BlocksWrite)
            return Result.Stale($"Overlap gewijzigd sinds voorstel: {overlap.ExplanationNl}");

        var otherPauseRows = sameDayRows
            .Select(row => new PayrollDailyPauseRules.DayPauseInput(
                row.End - row.Start,
                row.RegisteredPause))
            .ToList();
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            otherPauseRows,
            proposal.ProposedEnd - proposal.ProposedStart);
        if (!PauseEquals(plan.PauseToAssignOnTargetRow, proposal.ProposedPause))
        {
            return Result.Stale(
                "Dagpauze gewijzigd sinds voorstel "
                + $"(vereist nu {PayrollDailyPauseRules.Format(plan.PauseToAssignOnTargetRow)}, "
                + $"voorstel {PayrollDailyPauseRules.Format(proposal.ProposedPause)}); nieuw voorstel vereist.");
        }

        // After mutation, daily minimum must be satisfied.
        var afterPause = SumPause(sameDayRows) + proposal.ProposedPause;
        var afterGross = SumGross(sameDayRows) + (proposal.ProposedEnd - proposal.ProposedStart);
        var required = afterGross > PayrollDailyPauseRules.FourHourThreshold
            ? PayrollDailyPauseRules.MandatoryMinimumPause
            : TimeSpan.Zero;
        if (required > TimeSpan.Zero && afterPause < required)
        {
            return Result.Stale(
                "Dag langer dan 4 uur zonder voldoende pauze na create; nieuw voorstel vereist.");
        }

        return Result.Safe();
    }

    public static Result ValidateAdjust(
        IReadOnlyList<SameDayRow> sameDayRows,
        AdjustCheck proposal)
    {
        var target = sameDayRows.FirstOrDefault(row => row.PerformanceId == proposal.PerformanceId);
        if (target is null)
            return Result.Stale("Prestatie bestaat niet meer in Plenion; nieuw voorstel vereist.");

        if (target.Start != proposal.ExpectedCurrentStart
            || target.End != proposal.ExpectedCurrentEnd)
        {
            return Result.Stale("Huidige VAN/TOT wijkt af van snapshot; nieuw voorstel vereist.");
        }

        if (proposal.ExpectedCurrentPause is { } expectedPause
            && target.RegisteredPause != expectedPause)
        {
            return Result.Stale("Huidige PAUZE wijkt af van snapshot; nieuw voorstel vereist.");
        }

        if (proposal.ProposedEnd <= proposal.ProposedStart)
            return Result.Stale("Voorstel heeft geen positieve duur; nieuw voorstel vereist.");

        var others = sameDayRows
            .Where(row => row.PerformanceId != proposal.PerformanceId)
            .ToList();
        var overlap = PayrollWriteOverlapSafety.EvaluateCreateOrAdjust(
            proposal.ProposedStart,
            proposal.ProposedEnd,
            others.Select(row => new PayrollWriteOverlapSafety.Interval(row.Start, row.End, row.PerformanceId))
                .ToList());
        if (overlap.BlocksWrite)
            return Result.Stale($"Overlap gewijzigd sinds voorstel: {overlap.ExplanationNl}");

        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            others.Select(row => new PayrollDailyPauseRules.DayPauseInput(
                row.End - row.Start,
                row.RegisteredPause)).ToList(),
            proposal.ProposedEnd - proposal.ProposedStart,
            target.RegisteredPause);
        var resolvedPause = proposal.ProposedPause ?? target.RegisteredPause;
        if (proposal.ProposedPause is not null
            && !PauseEquals(plan.PauseToAssignOnTargetRow, proposal.ProposedPause.Value))
        {
            return Result.Stale(
                "Dagpauze gewijzigd sinds voorstel "
                + $"(vereist nu {PayrollDailyPauseRules.Format(plan.PauseToAssignOnTargetRow)}, "
                + $"voorstel {PayrollDailyPauseRules.Format(proposal.ProposedPause.Value)}); nieuw voorstel vereist.");
        }

        var afterPause = SumPause(others) + resolvedPause;
        var afterGross = SumGross(others) + (proposal.ProposedEnd - proposal.ProposedStart);
        var required = afterGross > PayrollDailyPauseRules.FourHourThreshold
            ? PayrollDailyPauseRules.MandatoryMinimumPause
            : TimeSpan.Zero;
        if (required > TimeSpan.Zero && afterPause < required)
        {
            return Result.Stale(
                "Dag langer dan 4 uur zonder voldoende pauze na correctie; nieuw voorstel vereist.");
        }

        return Result.Safe();
    }

    private static bool PauseEquals(TimeSpan a, TimeSpan b) =>
        Math.Abs((a - b).TotalSeconds) < 1;

    private static TimeSpan SumPause(IEnumerable<SameDayRow> rows)
    {
        var total = TimeSpan.Zero;
        foreach (var row in rows)
        {
            if (row.RegisteredPause > TimeSpan.Zero)
                total += row.RegisteredPause;
        }

        return total;
    }

    private static TimeSpan SumGross(IEnumerable<SameDayRow> rows)
    {
        var total = TimeSpan.Zero;
        foreach (var row in rows)
        {
            var gross = row.End - row.Start;
            if (gross > TimeSpan.Zero)
                total += gross;
        }

        return total;
    }
}

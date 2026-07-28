namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class PilotDeviationRules
{
    public static PilotDeviationAssessment Evaluate(
        DateTimeOffset firstPlenionStart,
        DateTimeOffset firstWorkLocationArrival,
        DateTimeOffset lastPlenionEnd,
        DateTimeOffset lastWorkLocationDeparture,
        int ignoreDifferenceMinutes)
    {
        var startDifference = WholeMinutes(
            firstWorkLocationArrival - firstPlenionStart);
        var endDifference = WholeMinutes(
            lastPlenionEnd - lastWorkLocationDeparture);
        var startRelevant = startDifference > ignoreDifferenceMinutes;
        var endRelevant = endDifference > ignoreDifferenceMinutes;
        return new PilotDeviationAssessment(
            startDifference,
            endDifference,
            startRelevant,
            endRelevant,
            (startRelevant ? Math.Max(0, startDifference) : 0) +
            (endRelevant ? Math.Max(0, endDifference) : 0));
    }

    private static int WholeMinutes(TimeSpan value) =>
        (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);
}

internal sealed record PilotDeviationAssessment(
    int StartDifferenceMinutes,
    int EndDifferenceMinutes,
    bool StartRelevant,
    bool EndRelevant,
    int PossibleEmployeeBenefitMinutes);

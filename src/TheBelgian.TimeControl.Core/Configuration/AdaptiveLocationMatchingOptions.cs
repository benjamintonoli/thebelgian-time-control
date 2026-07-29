namespace TheBelgian.TimeControl.Core.Configuration;

public sealed class AdaptiveLocationMatchingOptions
{
    public const string SectionName = "AdaptiveLocationMatching";

    public double StrongDistanceMeters { get; init; } = 100;
    public double ProbableDistanceMeters { get; init; } = 250;
    public double MaximumLearnedClusterDistanceMeters { get; init; } = 500;
    public double MinimumOverlapMinutes { get; init; } = 5;
    public double MinimumOverlapPercent { get; init; } = 20;
    public double StrongOverlapPercent { get; init; } = 50;
    public int MaximumArrivalDifferenceMinutes { get; init; } = 30;
    public int MaximumDepartureDifferenceMinutes { get; init; } = 30;
    public int MinimumStopDurationMinutes { get; init; } = 3;
    public int MinimumDistinctWorkdays { get; init; } = 3;
    public double MinimumDominancePercentage { get; init; } = 80;
    public double MaximumDistanceFromPlenionMeters { get; init; } = 500;
    public double MinimumScoreMargin { get; init; } = 8;
    public double ConfirmedMinimumScore { get; init; } = 70;
    public double ProbableMinimumScore { get; init; } = 55;
    public double PassThroughMaxDurationMinutes { get; init; } = 2;
    public double StopMergeDistanceMeters { get; init; } = 60;
    public string CalculationVersion { get; init; } = "adaptive-v1";

    public void Validate()
    {
        if (StrongDistanceMeters <= 0 ||
            ProbableDistanceMeters <= StrongDistanceMeters ||
            MaximumLearnedClusterDistanceMeters < ProbableDistanceMeters)
        {
            throw new InvalidOperationException(
                "Adaptieve afstandsgrenzen moeten positief en oplopend zijn.");
        }
    }
}

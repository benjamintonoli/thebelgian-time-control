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
    public string CalculationVersion { get; init; } = "adaptive-v2-visit";

    /// <summary>
    /// Max distance between consecutive stop fragments that may form one VisitCandidate.
    /// </summary>
    public double VisitMergeDistanceMeters { get; init; } = 100;

    /// <summary>
    /// Max gap between fragment departure and next fragment arrival for visit aggregation.
    /// </summary>
    public double VisitMergeMaxGapMinutes { get; init; } = 15;

    /// <summary>
    /// When adaptive leaves a strong candidate Unresolved/Ambiguous, allow precision-preserving recovery.
    /// </summary>
    public bool EnablePrecisionPreservingRecovery { get; init; } = true;

    /// <summary>
    /// Recovery never accepts candidates farther than this (keeps Learned251To500 out without clusters).
    /// </summary>
    public double RecoveryMaximumDistanceMeters { get; init; } = 250;

    /// <summary>
    /// Minimum positive overlap minutes OR percent required for recovery (OR-combined).
    /// </summary>
    public double RecoveryMinimumOverlapMinutes { get; init; } = 10;

    /// <summary>
    /// Minimum overlap percent OR minutes required for recovery (OR-combined).
    /// </summary>
    public double RecoveryMinimumOverlapPercent { get; init; } = 50;

    /// <summary>
    /// Strong temporal support for Probable101To250 recovery with weaker geocode (StreetOnly).
    /// </summary>
    public double RecoveryStrongOverlapMinutes { get; init; } = 30;

    /// <summary>
    /// Strong temporal support percent for Probable101To250 recovery with weaker geocode.
    /// </summary>
    public double RecoveryStrongOverlapPercent { get; init; } = 50;

    /// <summary>
    /// Top candidate must beat the runner-up by at least this score margin.
    /// </summary>
    public double RecoveryMinimumScoreMargin { get; init; } = 8;

    /// <summary>
    /// Per-performance minimum overlap for the short consecutive same-LACLEUNIK chain exception.
    /// </summary>
    public double RecoveryShortChainMinOverlapMinutes { get; init; } = 3;

    /// <summary>
    /// Combined short-chain coverage: share of the visit duration that falls inside the
    /// adjacent same-LACLEUNIK performance chain window (percent).
    /// </summary>
    public double RecoveryShortChainMinCombinedOverlapPercent { get; init; } = 80;

    public void Validate()
    {
        if (StrongDistanceMeters <= 0 ||
            ProbableDistanceMeters <= StrongDistanceMeters ||
            MaximumLearnedClusterDistanceMeters < ProbableDistanceMeters)
        {
            throw new InvalidOperationException(
                "Adaptieve afstandsgrenzen moeten positief en oplopend zijn.");
        }

        if (VisitMergeDistanceMeters <= 0 || VisitMergeMaxGapMinutes < 0)
        {
            throw new InvalidOperationException(
                "Visit-merge drempels moeten positief zijn.");
        }

        if (RecoveryMaximumDistanceMeters < StrongDistanceMeters ||
            RecoveryMinimumOverlapMinutes < 0 ||
            RecoveryMinimumOverlapPercent < 0 ||
            RecoveryStrongOverlapMinutes < RecoveryMinimumOverlapMinutes ||
            RecoveryStrongOverlapPercent < RecoveryMinimumOverlapPercent ||
            RecoveryMinimumScoreMargin < 0 ||
            RecoveryShortChainMinOverlapMinutes < 0 ||
            RecoveryShortChainMinCombinedOverlapPercent <= 0)
        {
            throw new InvalidOperationException(
                "Recovery-drempels moeten niet-negatief zijn en sterke overlap >= minimum overlap.");
        }
    }
}

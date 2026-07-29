using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Compatibility wrapper: matching pipelines consume MergedPilotStop built from VisitCandidates.
/// </summary>
internal static class MergedStopBuilder
{
    public static IReadOnlyList<MergedPilotStop> Merge(
        IReadOnlyList<PilotStop> stops,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator) =>
        VisitCandidateBuilder.BuildMerged(stops, options, distanceCalculator);
}

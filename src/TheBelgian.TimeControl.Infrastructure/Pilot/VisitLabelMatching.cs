using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class VisitLabelMatching
{
    public static IReadOnlyList<string> ResolveExpectedStopIds(
        string? expectedStopId,
        IReadOnlyList<string>? expectedVisitStopIds)
    {
        if (expectedVisitStopIds is { Count: > 0 })
        {
            return expectedVisitStopIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(expectedStopId))
        {
            return [expectedStopId.Trim()];
        }

        return [];
    }

    public static bool MatchesVisit(
        string? expectedStopId,
        IReadOnlyList<string>? expectedVisitStopIds,
        string? predictedStopId,
        IReadOnlyList<string> predictedSourceStopIds)
    {
        var expected = ResolveExpectedStopIds(expectedStopId, expectedVisitStopIds);
        if (expected.Count == 0)
        {
            return false;
        }

        var predicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in predictedSourceStopIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                predicted.Add(id);
            }
        }

        if (!string.IsNullOrWhiteSpace(predictedStopId))
        {
            predicted.Add(predictedStopId);
        }

        return expected.All(predicted.Contains);
    }
}

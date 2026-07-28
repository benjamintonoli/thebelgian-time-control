using System.ComponentModel.DataAnnotations;

namespace TheBelgian.TimeControl.Core.Configuration;

public sealed class MatchingOptions
{
    public const string SectionName = "Matching";

    [Range(0, 120)]
    public int IgnoreDifferenceMinutes { get; init; } = 3;

    [Range(1, 120)]
    public int PatternDifferenceMinutes { get; init; } = 5;

    [Range(1, 240)]
    public int IndividualExceptionMinutes { get; init; } = 15;

    [Range(1, 480)]
    public int HighPriorityExceptionMinutes { get; init; } = 30;

    [Range(1, 365)]
    public int PatternWindowDays { get; init; } = 20;

    [Range(1, 365)]
    public int PatternMinimumOccurrences { get; init; } = 8;

    [Range(1, 10000)]
    public int PatternCumulativeMinutes { get; init; } = 60;

    public void Validate()
    {
        if (IgnoreDifferenceMinutes < 0 ||
            PatternWindowDays <= 0 ||
            PatternMinimumOccurrences <= 0 ||
            PatternCumulativeMinutes <= 0)
        {
            throw new InvalidOperationException("Matchingtoleranties moeten positieve waarden bevatten.");
        }

        if (PatternDifferenceMinutes <= IgnoreDifferenceMinutes ||
            IndividualExceptionMinutes < PatternDifferenceMinutes ||
            HighPriorityExceptionMinutes < IndividualExceptionMinutes)
        {
            throw new InvalidOperationException("Matchingtoleranties moeten oplopend zijn.");
        }
    }
}

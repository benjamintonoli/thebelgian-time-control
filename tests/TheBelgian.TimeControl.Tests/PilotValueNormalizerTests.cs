using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class PilotValueNormalizerTests
{
    [Fact]
    public void ParseDuration_InfersSecondsFromElapsedTime()
    {
        var observations = new List<string>();

        var result = PilotValueNormalizer.ParseDuration(
            "1800",
            TimeSpan.FromMinutes(30),
            "duration",
            observations);

        Assert.Equal(30, result.Minutes);
        Assert.Equal(PilotNumericUnit.Seconds, result.Unit);
    }

    [Fact]
    public void ParseOptionalDuration_ReusesDetectedDurationUnit()
    {
        var observations = new List<string>();

        var result = PilotValueNormalizer.ParseOptionalDuration(
            "600",
            PilotNumericUnit.Seconds,
            "stoppedafter",
            observations);

        Assert.Equal(10, result.Minutes);
    }

    [Fact]
    public void ParseDistance_ConvertsExplicitMetres()
    {
        var observations = new List<string>();

        var result = PilotValueNormalizer.ParseDistance("1250 m", observations);

        Assert.Equal(1.25m, result.Kilometres);
    }

    [Fact]
    public void ParseTimestamp_UsesBrusselsSummerOffset()
    {
        var observations = new List<string>();

        var result = PilotValueNormalizer.ParseTimestamp(
            "22/07/2026",
            "08:15:00",
            "start",
            observations);

        Assert.Equal(TimeSpan.FromHours(2), result.Offset);
    }

    [Fact]
    public void ParseTimestamp_AcceptsDuplicatedCompleteTimestampFields()
    {
        var observations = new List<string>();

        var result = PilotValueNormalizer.ParseTimestamp(
            "2026-07-22 08:54:38",
            "2026-07-22 08:54:38",
            "start",
            observations);

        Assert.Equal(new DateTime(2026, 7, 22, 8, 54, 38), result.DateTime);
    }
}

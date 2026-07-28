using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class PilotLocationMatcherTests
{
    private static readonly MatchingOptions Options = new()
    {
        IgnoreDifferenceMinutes = 3,
        PatternDifferenceMinutes = 5,
        IndividualExceptionMinutes = 15,
        HighPriorityExceptionMinutes = 30,
    };

    [Fact]
    public void Match_ReturnsExactAddressMatchWithTimeOverlap()
    {
        var performance = Performance(
            "Teststraat 1",
            "9000",
            "Gent");
        var stop = Stop(
            "stop-1",
            "Teststraat 1, 9000 Gent, België",
            performance.StartDateTime.AddMinutes(-5),
            performance.EndDateTime.AddMinutes(5));

        var result = PilotLocationMatcher.Match(
            [performance],
            [stop],
            Options);

        Assert.Equal(PilotMatchStatus.ExactAddressMatch, result[0].Status);
        Assert.Equal("stop-1", result[0].MatchedStop?.StopId);
        Assert.Equal(100, result[0].ConfidenceScore);
    }

    [Fact]
    public void Match_DoesNotChooseBetweenEquivalentTopCandidates()
    {
        var performance = Performance(
            "Teststraat 1",
            "9000",
            "Gent");
        var first = Stop(
            "stop-1",
            "Teststraat 1, 9000 Gent, België",
            performance.StartDateTime.AddMinutes(-5),
            performance.EndDateTime.AddMinutes(5));
        var second = first with { StopId = "stop-2" };

        var result = PilotLocationMatcher.Match(
            [performance],
            [first, second],
            Options);

        Assert.Equal(PilotMatchStatus.Ambiguous, result[0].Status);
        Assert.Null(result[0].MatchedStop);
        Assert.Equal(2, result[0].Alternatives.Count);
    }

    [Fact]
    public void Match_UsesTimeOnlyWhenPlenionHasNoAddress()
    {
        var performance = Performance(null, null, null);
        var stop = Stop(
            "stop-1",
            "Andereweg 9, 9000 Gent, België",
            performance.StartDateTime,
            performance.EndDateTime);

        var result = PilotLocationMatcher.Match(
            [performance],
            [stop],
            Options);

        Assert.Equal(PilotMatchStatus.TimeOnlyMatch, result[0].Status);
        Assert.Equal(30, result[0].ConfidenceScore);
    }

    [Fact]
    public void Match_ComparesStreetNameWithoutHouseNumber()
    {
        var performance = Performance(
            "Teststraat 70",
            "9000",
            "Gent");
        var stop = Stop(
            "stop-1",
            "Teststraat 60, 9000 Gent, België",
            performance.StartDateTime,
            performance.EndDateTime) with
        {
            Street = "Teststraat 60",
        };

        var result = PilotLocationMatcher.Match(
            [performance],
            [stop],
            Options);

        Assert.Equal(PilotMatchStatus.ProbableAddressMatch, result[0].Status);
        Assert.Contains("straatnaam gelijk", result[0].Reasons);
    }

    private static NormalizedPilotPerformance Performance(
        string? street,
        string? postalCode,
        string? city)
    {
        var start = new DateTimeOffset(
            2026,
            7,
            23,
            8,
            0,
            0,
            TimeSpan.FromHours(2));
        return new NormalizedPilotPerformance(
            1,
            "14",
            new DateOnly(2026, 7, 23),
            start,
            start.AddHours(1),
            0,
            60,
            60,
            0,
            "10",
            "34",
            "100",
            "Werk",
            null,
            "P-1",
            "Project",
            "A-1",
            "Klant",
            street,
            postalCode,
            city,
            "België",
            1,
            1,
            1,
            "Uniek.",
            "Test");
    }

    private static PilotStop Stop(
        string id,
        string address,
        DateTimeOffset arrival,
        DateTimeOffset departure) =>
        new(
            id,
            DateOnly.FromDateTime(arrival.DateTime),
            "in",
            "out",
            arrival,
            departure,
            (int)(departure - arrival).TotalMinutes,
            address,
            "9000",
            "Gent",
            "Teststraat 1",
            null,
            null,
            null,
            null,
            "TEST",
            "driver-test",
            "Testtechnieker",
            true,
            "Continu");
}

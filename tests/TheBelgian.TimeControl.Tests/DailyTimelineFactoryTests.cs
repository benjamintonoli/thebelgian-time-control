using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Tests;

public sealed class DailyTimelineFactoryTests
{
    [Theory]
    [InlineData("Verplaatsing naar klant")]
    [InlineData("RIJTIJD")]
    [InlineData("Transport materiaal")]
    public void IsTravelPerformance_RecognizesConfiguredMarkers(string description)
    {
        var performance = new PlenionPerformance { Description = description };

        Assert.True(DailyTimelineFactory.IsTravelPerformance(performance));
    }

    [Fact]
    public void Create_SelectsFirstAndLastTimesAndSumsDurations()
    {
        var date = new DateOnly(2026, 7, 20);
        var technician = new Technician
        {
            ExternalId = "tech-42",
            Name = "Voorbeeld Technieker",
        };
        var performances = new[]
        {
            Performance(100, date, 8, 0, 9, 0, "Verplaatsing", 0),
            Performance(101, date, 9, 0, 17, 0, "Werk", 30),
        };
        var trips = new[]
        {
            Trip("a", 8, 10, 8, 40, 30),
            Trip("b", 16, 20, 16, 50, 30),
        };

        var result = DailyTimelineFactory.Create(technician, date, performances, trips);

        Assert.Equal(At(8, 0), result.PlenionStart);
        Assert.Equal(At(17, 0), result.PlenionEnd);
        Assert.Equal(510, result.RegisteredMinutes);
        Assert.Equal(60, result.RegisteredTravelMinutes);
        Assert.Equal(At(8, 10), result.FirstTripStart);
        Assert.Equal(At(16, 50), result.LastTripEnd);
        Assert.Equal(60, result.DrivingMinutes);
    }

    private static PlenionPerformance Performance(
        long id,
        DateOnly date,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        string description,
        int breakMinutes) =>
        new()
        {
            ExternalId = id,
            TechnicianExternalId = "tech-42",
            Date = date,
            Start = At(startHour, startMinute),
            End = At(endHour, endMinute),
            Description = description,
            BreakMinutes = breakMinutes,
        };

    private static PowerfleetTrip Trip(
        string id,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        int durationMinutes) =>
        new()
        {
            ExternalId = id,
            DriverId = "tech-42",
            Start = At(startHour, startMinute),
            End = At(endHour, endMinute),
            DurationMinutes = durationMinutes,
        };

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 7, 20, hour, minute, 0, TimeSpan.FromHours(2));
}

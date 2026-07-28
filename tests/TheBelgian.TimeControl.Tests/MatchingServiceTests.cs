using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Tests;

public sealed class MatchingServiceTests
{
    private readonly TimeControlMatchingService _service =
        new(new MatchingOptions());

    [Fact]
    public void Detect_IgnoresDifferenceThroughThreeMinutes()
    {
        var result = _service.Detect(Timeline(startDifference: 3));

        Assert.Single(result);
        Assert.Equal(ExceptionType.None, result[0].Type);
    }

    [Fact]
    public void Detect_ShowsIndividualExceptionFromFifteenMinutes()
    {
        var result = _service.Detect(Timeline(startDifference: 15));

        var exception = Assert.Single(result);
        Assert.Equal(ExceptionType.RegisteredStartTooEarly, exception.Type);
        Assert.Equal(ExceptionPriority.Normal, exception.Priority);
    }

    [Fact]
    public void Detect_MarksThirtyMinutesAsHighPriority()
    {
        var exception = Assert.Single(_service.Detect(Timeline(endDifference: 30)));

        Assert.Equal(ExceptionType.RegisteredEndTooLate, exception.Type);
        Assert.Equal(ExceptionPriority.High, exception.Priority);
    }

    [Fact]
    public void Detect_MarksEightSmallOccurrencesAndSixtyMinutesAsPattern()
    {
        var history = Enumerable.Range(1, 7)
            .Select(day => Historical(new DateOnly(2026, 7, day), 8))
            .ToArray();

        var result = _service.Detect(
            Timeline(date: new DateOnly(2026, 7, 20), startDifference: 8),
            history);

        Assert.Contains(result, item => item.Type == ExceptionType.StructuralPattern);
    }

    [Fact]
    public void Detect_DoesNotDoubleCountMultipleExceptionsOnSameDayForPattern()
    {
        var history = Enumerable.Range(1, 7)
            .SelectMany(day => new[]
            {
                Historical(new DateOnly(2026, 7, day), 5, ExceptionType.RegisteredStartTooEarly),
                Historical(new DateOnly(2026, 7, day), 5, ExceptionType.RegisteredEndTooLate),
            })
            .ToArray();

        var result = _service.Detect(
            Timeline(date: new DateOnly(2026, 7, 20), startDifference: 5),
            history);

        Assert.DoesNotContain(result, item => item.Type == ExceptionType.StructuralPattern);
    }

    [Fact]
    public void Detect_UsesUncertainVehicleStatusWithoutCallingItAnError()
    {
        var result = _service.Detect(Timeline(hasCertainVehicleAssignment: false));

        var exception = Assert.Single(result);
        Assert.Equal(ExceptionType.UncertainVehicleAssignment, exception.Type);
        Assert.Equal(ExceptionPriority.Low, exception.Priority);
    }

    private static DailyTechnicianTimeline Timeline(
        DateOnly? date = null,
        int startDifference = 0,
        int endDifference = 0,
        int travelDifference = 0,
        bool hasCertainVehicleAssignment = true)
    {
        var plenionStart = At(8, 0);
        var plenionEnd = At(17, 0);
        return new DailyTechnicianTimeline
        {
            TechnicianExternalId = "tech-1",
            TechnicianName = "Test Technieker",
            Date = date ?? new DateOnly(2026, 7, 20),
            PlenionStart = plenionStart,
            PlenionEnd = plenionEnd,
            FirstTripStart = plenionStart.AddMinutes(startDifference),
            LastTripEnd = plenionEnd.AddMinutes(-endDifference),
            RegisteredMinutes = 480,
            BreakMinutes = 30,
            RegisteredTravelMinutes = 60 + travelDifference,
            DrivingMinutes = 60,
            HasCertainVehicleAssignment = hasCertainVehicleAssignment,
        };
    }

    private static DetectedException Historical(
        DateOnly date,
        int difference,
        ExceptionType type = ExceptionType.RegisteredStartTooEarly) =>
        new()
        {
            ExternalKey = $"tech-1:{date:yyyyMMdd}:{type}",
            TechnicianExternalId = "tech-1",
            TechnicianName = "Test Technieker",
            Date = date,
            Type = type,
            DifferenceMinutes = difference,
        };

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 7, 20, hour, minute, 0, TimeSpan.FromHours(2));
}

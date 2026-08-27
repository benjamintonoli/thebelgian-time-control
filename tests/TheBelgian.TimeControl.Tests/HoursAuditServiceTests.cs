using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class HoursAuditServiceTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    public void PositiveWholeMinutes_OnlyReturnsPositiveDeviation(
        int minutes,
        int expected)
    {
        Assert.Equal(
            expected,
            HoursAuditService.PositiveWholeMinutes(TimeSpan.FromMinutes(minutes)));
    }
}

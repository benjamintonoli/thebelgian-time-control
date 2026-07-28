using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Tests;

public sealed class TimeIntervalTests
{
    [Fact]
    public void Overlap_ReturnsOnlySharedTime()
    {
        var first = new TimeInterval(At(8, 0), At(10, 0));
        var second = new TimeInterval(At(9, 30), At(11, 0));

        Assert.Equal(TimeSpan.FromMinutes(30), first.Overlap(second));
    }

    [Fact]
    public void Overlap_ReturnsZeroForTouchingIntervals()
    {
        var first = new TimeInterval(At(8, 0), At(9, 0));
        var second = new TimeInterval(At(9, 0), At(10, 0));

        Assert.Equal(TimeSpan.Zero, first.Overlap(second));
    }

    [Fact]
    public void Duration_RejectsAnEndBeforeStart()
    {
        var interval = new TimeInterval(At(10, 0), At(9, 0));

        Assert.Throws<InvalidOperationException>(() => interval.Duration);
    }

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 7, 20, hour, minute, 0, TimeSpan.FromHours(2));
}

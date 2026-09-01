using TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PerformanceTimeNormalizerTests
{
    [Fact]
    public void SameDay_ProducesEightHoursThirtyMinutesGross()
    {
        var result = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            "08:00",
            "16:30");

        Assert.NotNull(result.Start);
        Assert.NotNull(result.End);
        Assert.Equal(new TimeSpan(8, 30, 0), result.GrossClockDuration);
        Assert.Equal(new DateOnly(2026, 7, 1), DateOnly.FromDateTime(result.Start!.Value.DateTime));
        Assert.Equal(new DateOnly(2026, 7, 1), DateOnly.FromDateTime(result.End!.Value.DateTime));
    }

    [Fact]
    public void Overnight_EndsNextCalendarDay()
    {
        var result = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            "22:00",
            "06:00");

        Assert.NotNull(result.Start);
        Assert.NotNull(result.End);
        Assert.Equal(TimeSpan.FromHours(8), result.GrossClockDuration);
        Assert.Equal(new DateOnly(2026, 7, 1), DateOnly.FromDateTime(result.Start!.Value.DateTime));
        Assert.Equal(new DateOnly(2026, 7, 2), DateOnly.FromDateTime(result.End!.Value.DateTime));
    }

    [Fact]
    public void BlankVan_StartIsNull()
    {
        var result = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            null,
            "16:30");

        Assert.Null(result.Start);
        Assert.NotNull(result.End);
        Assert.Null(result.GrossClockDuration);
    }

    [Fact]
    public void BlankTot_EndIsNull()
    {
        var result = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            "08:00",
            "  ");

        Assert.NotNull(result.Start);
        Assert.Null(result.End);
        Assert.Null(result.GrossClockDuration);
    }

    [Fact]
    public void StartEqualsEnd_GrossDurationIsZero_NotTwentyFourHours()
    {
        var result = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            "12:00",
            "12:00");

        Assert.Equal(TimeSpan.Zero, result.GrossClockDuration);
        Assert.Equal(result.Start, result.End);
    }

    [Fact]
    public void ExcelTimeExport_UsesTimeOfDayOnDatum()
    {
        var result = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 3),
            "1899-12-30 10:35:00",
            "1899-12-30 12:30:00");

        Assert.Equal(new TimeSpan(1, 55, 0), result.GrossClockDuration);
        Assert.Equal(new DateOnly(2026, 7, 3), DateOnly.FromDateTime(result.Start!.Value.DateTime));
        Assert.DoesNotContain("1899", result.Start!.Value.ToString("O"));
    }

    [Fact]
    public void Normalize_ObjectClockValues_MatchesStringPath()
    {
        var fromObjects = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(16).Add(TimeSpan.FromMinutes(30)));
        var fromStrings = PerformanceTimeNormalizer.Normalize(
            new DateOnly(2026, 7, 1),
            "08:00:00",
            "16:30:00");

        Assert.Equal(fromStrings.GrossClockDuration, fromObjects.GrossClockDuration);
        Assert.Equal(fromStrings.Start, fromObjects.Start);
        Assert.Equal(fromStrings.End, fromObjects.End);
    }

    [Fact]
    public void AtlMinutesExact_PreservesDecimalPrecisionWithoutRounding()
    {
        const decimal atlHours = 8.83m;
        var minutes = PerformanceTimeNormalizer.AtlMinutesExact(atlHours);

        Assert.Equal(529.8m, minutes);
        Assert.Equal(atlHours * 60m, minutes);
    }
}

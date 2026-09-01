using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PauseNormalizerTests
{
    [Fact]
    public void Null_IsMissing()
    {
        var result = PauseNormalizer.Normalize(null);

        Assert.Equal(PauseParseStatus.Missing, result.Status);
        Assert.Null(result.ExactMinutes);
    }

    [Theory]
    [InlineData("00:00:00", 0)]
    [InlineData("00:15:00", 15)]
    [InlineData("00:30:00", 30)]
    [InlineData("00:45:00", 45)]
    public void TimeOfDayClock_MapsExactMinutes(string raw, int expectedMinutes)
    {
        var result = PauseNormalizer.NormalizeText(raw);

        Assert.Equal(PauseParseStatus.Valid, result.Status);
        Assert.Equal(expectedMinutes, result.ExactMinutes);
        Assert.Equal(PauseSourceKind.TimeOfDay, result.SourceKind);
    }

    [Fact]
    public void ExcelDateTimeExport_UsesTimeOfDayOnly()
    {
        var result = PauseNormalizer.NormalizeText("1899-12-30 00:30:00");

        Assert.Equal(PauseParseStatus.Valid, result.Status);
        Assert.Equal(30m, result.ExactMinutes);
        Assert.Equal(PauseSourceKind.TimeOfDay, result.SourceKind);
    }

    [Fact]
    public void TimeSpanObject_IsValid()
    {
        var result = PauseNormalizer.Normalize(TimeSpan.FromMinutes(30));

        Assert.Equal(PauseParseStatus.Valid, result.Status);
        Assert.Equal(30m, result.ExactMinutes);
    }

    [Fact]
    public void NumericWithoutKind_IsInvalid_NotSilentlyInterpreted()
    {
        var result = PauseNormalizer.Normalize(0.5m, PauseSourceKind.Unspecified);

        Assert.Equal(PauseParseStatus.Invalid, result.Status);
        Assert.Null(result.ExactMinutes);
    }

    [Fact]
    public void NumericAsHours_UsesDirectHours()
    {
        var result = PauseNormalizer.Normalize(0.75m, PauseSourceKind.Hours);

        Assert.Equal(PauseParseStatus.Valid, result.Status);
        Assert.Equal(45m, result.ExactMinutes);
        Assert.Equal(PauseSourceKind.Hours, result.SourceKind);
    }

    [Fact]
    public void NumericAsExcelDayFraction_MultipliesBy24ForHours()
    {
        // Exact Excel day fraction for 6 hours = 0.25 day.
        var result = PauseNormalizer.Normalize(0.25m, PauseSourceKind.ExcelDayFraction);

        Assert.Equal(PauseParseStatus.Valid, result.Status);
        Assert.Equal(360m, result.ExactMinutes);
        Assert.Equal(PauseSourceKind.ExcelDayFraction, result.SourceKind);
    }

    [Fact]
    public void NumericAsExcelDayFraction_HalfHour_UsesHistoricalPowerQueryScale()
    {
        // 00:30:00 ≈ 30/1440 of a day; allow tiny decimal residue.
        var dayFraction = 30m / 1440m;
        var result = PauseNormalizer.Normalize(dayFraction, PauseSourceKind.ExcelDayFraction);

        Assert.Equal(PauseParseStatus.Valid, result.Status);
        Assert.NotNull(result.ExactMinutes);
        Assert.True(
            Math.Abs(result.ExactMinutes.Value - 30m) < 0.000001m,
            $"ExactMinutes={result.ExactMinutes}");
    }

    [Fact]
    public void EmptyString_IsMissing()
    {
        var result = PauseNormalizer.NormalizeText("   ");

        Assert.Equal(PauseParseStatus.Missing, result.Status);
    }

    [Fact]
    public void GarbageText_IsInvalid()
    {
        var result = PauseNormalizer.NormalizeText("not-a-pause");

        Assert.Equal(PauseParseStatus.Invalid, result.Status);
        Assert.Null(result.ExactMinutes);
    }
}

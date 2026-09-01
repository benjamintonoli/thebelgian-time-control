using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollPerformanceMapperTests
{
    [Fact]
    public void Map_NormalWorkRow_PreservesAtlPrecisionAndFlags()
    {
        var row = new PlenionPayrollPerformanceRow(
            1001,
            new DateOnly(2026, 7, 1),
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(16).Add(TimeSpan.FromMinutes(30)),
            TimeSpan.FromMinutes(30),
            8.83m,
            120m,
            "495",
            "PROJ1",
            9,
            "BON123",
            "Work",
            null,
            null,
            100);

        var entry = PayrollPerformanceMapper.Map(row);

        Assert.Equal(1001, entry.SourceEntryId);
        Assert.Equal("1001", entry.SourceEntryKey);
        Assert.Equal("495", entry.ResourceId);
        Assert.Equal(8.83m, entry.AtlHoursRaw);
        Assert.Equal(529.8m, entry.AtlMinutesExact);
        Assert.Equal(TimeSpan.FromHours(8).Add(TimeSpan.FromMinutes(30)), entry.GrossClockDuration);
        Assert.Equal(PauseParseStatus.Valid, entry.Pause.Status);
        Assert.Equal(30m, entry.Pause.ExactMinutes);
        Assert.Equal(120m, entry.Km);
        Assert.Equal(9, entry.HfdTaakId);
        Assert.Equal(100, entry.ProjectNumber);
        Assert.False(entry.IsTravel);
        Assert.False(entry.IsStandby);
        Assert.False(entry.IsAbsence);
        Assert.Equal(1001, entry.SortKey);
    }

    [Fact]
    public void Map_TravelRow_SetsTravelFlag()
    {
        var row = BaseRow with { IdHfdTaak = 5, AtlHoursRaw = 0.25m, Omschr = "Travel" };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.True(entry.IsTravel);
        Assert.False(entry.IsStandby);
    }

    [Fact]
    public void Map_StandbyRow_SetsStandbyFlag()
    {
        var row = BaseRow with { IdHfdTaak = 23, AtlHoursRaw = 2m };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.True(entry.IsStandby);
        Assert.False(entry.IsTravel);
    }

    [Fact]
    public void Map_NullVanTot_StartEndAndGrossAreNull()
    {
        var row = BaseRow with { Van = null, Tot = null };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.Null(entry.Start);
        Assert.Null(entry.End);
        Assert.Null(entry.GrossClockDuration);
    }

    [Fact]
    public void Map_OvernightTot_AddsNextDay()
    {
        var row = BaseRow with
        {
            Van = TimeSpan.FromHours(22),
            Tot = TimeSpan.FromHours(6),
        };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.Equal(TimeSpan.FromHours(8), entry.GrossClockDuration);
        Assert.Equal(new DateOnly(2026, 7, 2), DateOnly.FromDateTime(entry.End!.Value.DateTime));
    }

    [Fact]
    public void Map_PauseTimeSpan_NormalizesValidPause()
    {
        var row = BaseRow with { Pauze = TimeSpan.FromMinutes(45) };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.Equal(PauseParseStatus.Valid, entry.Pause.Status);
        Assert.Equal(45m, entry.Pause.ExactMinutes);
    }

    [Fact]
    public void Map_PauseNull_IsMissing()
    {
        var row = BaseRow with { Pauze = null };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.Equal(PauseParseStatus.Missing, entry.Pause.Status);
    }

    [Fact]
    public void Map_NullKmAndBon_AllowsNulls()
    {
        var row = BaseRow with { Km = null, BonNr = null, Memo = null, Omschr = null };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.Null(entry.Km);
        Assert.Null(entry.BonNr);
        Assert.Null(entry.Description);
        Assert.Null(entry.Memo);
    }

    [Fact]
    public void Map_AbsenceLeave_SetsAbsenceFlag()
    {
        var row = BaseRow with { IdHfdTaak = 10 };
        var entry = PayrollPerformanceMapper.Map(row);
        Assert.True(entry.IsAbsence);
    }

    private static PlenionPayrollPerformanceRow BaseRow => new(
        2001,
        new DateOnly(2026, 7, 1),
        TimeSpan.FromHours(8),
        TimeSpan.FromHours(16),
        null,
        8m,
        null,
        "633",
        null,
        9,
        null,
        "Desc",
        "Memo",
        null,
        null);
}

using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyCalendarSynthesisTests
{
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateOnly Friday = new(2026, 7, 10);
    private static readonly DateOnly Saturday = new(2026, 7, 11);

    [Fact]
    public void ResourceExpansion_DirectResourceOnly()
    {
        var row = SourceRow(idResource: "19", resources: "22;25");
        Assert.Equal(["19"], LegacyCalendarSynthesis.ExpandResources(row));
    }

    [Fact]
    public void ResourceExpansion_ResourcesListWhenDirectMissing()
    {
        var row = SourceRow(idResource: null, resources: "19; 22 ;");
        Assert.Equal(["19", "22"], LegacyCalendarSynthesis.ExpandResources(row));
    }

    [Fact]
    public void ResourceExpansion_ZeroDirectUsesResources()
    {
        var row = SourceRow(idResource: "0", resources: "19;22");
        Assert.Equal(["19", "22"], LegacyCalendarSynthesis.ExpandResources(row));
    }

    [Fact]
    public void ResourceExpansion_EmptyWhenBothMissing()
    {
        var row = SourceRow(idResource: null, resources: null);
        Assert.Empty(LegacyCalendarSynthesis.ExpandResources(row));
    }

    [Fact]
    public void DateExpansion_SingleMonday()
    {
        var row = SourceRow(dateFrom: Monday, dateTo: null);
        Assert.Equal([Monday], LegacyCalendarSynthesis.ExpandDates(row).ToList());
    }

    [Fact]
    public void DateExpansion_MondayThroughWednesday()
    {
        var row = SourceRow(dateFrom: Monday, dateTo: Monday.AddDays(2));
        Assert.Equal(
            [Monday, Monday.AddDays(1), Monday.AddDays(2)],
            LegacyCalendarSynthesis.ExpandDates(row).ToList());
    }

    [Fact]
    public void DateExpansion_FridayToMonday_KeepsOnlyWeekdays()
    {
        var row = SourceRow(dateFrom: Friday, dateTo: Monday.AddDays(7));
        var dates = LegacyCalendarSynthesis.Synthesize([row]).Select(entry => entry.Date).ToList();
        Assert.Equal([Friday, Monday.AddDays(7)], dates);
    }

    [Fact]
    public void DateExpansion_SaturdayOnly_ProducesZeroRows()
    {
        var row = SourceRow(dateFrom: Saturday, dateTo: Saturday);
        Assert.Empty(LegacyCalendarSynthesis.Synthesize([row]));
    }

    [Fact]
    public void DateExpansion_ReverseEndDate_UsesStartOnly()
    {
        var row = SourceRow(dateFrom: Monday, dateTo: Monday.AddDays(-1));
        Assert.Equal([Monday], LegacyCalendarSynthesis.ExpandDates(row).ToList());
    }

    [Fact]
    public void Dedupe_RemovesExactDuplicateExpandedRows()
    {
        var row = SourceRow(idCalendar: 100, idResource: "19", dateFrom: Monday);
        var synthetic = LegacyCalendarSynthesis.Synthesize([row, row]);
        Assert.Single(synthetic);
    }

    [Fact]
    public void Dedupe_RetainsDifferentCalendarIdsSameResourceDate()
    {
        var rows = new[]
        {
            SourceRow(idCalendar: 100, idResource: "19", dateFrom: Monday, taskType: 5),
            SourceRow(idCalendar: 101, idResource: "19", dateFrom: Monday, taskType: 5),
        };
        Assert.Equal(2, LegacyCalendarSynthesis.Synthesize(rows).Count);
    }

    [Fact]
    public void StableIdentity_UsesExactKlFormat()
    {
        var synthetic = LegacyCalendarSynthesis.Synthesize(
            [SourceRow(idCalendar: 148743, idResource: "14", dateFrom: new DateOnly(2026, 7, 27))]).Single();
        Assert.Equal("KL148743_20260727_14", synthetic.StableSourceKey);
    }

    [Fact]
    public void TaskMapping_MapsTypesExactly()
    {
        Assert.Equal(18, LegacyCalendarSynthesis.MapHfdTaakId(3));
        Assert.Equal(10, LegacyCalendarSynthesis.MapHfdTaakId(5));
        Assert.Equal(10, LegacyCalendarSynthesis.MapHfdTaakId(8));
    }

    [Fact]
    public void HalfDay_MondayMorning_IsFourHours()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(8, 0), timeTo: new TimeOnly(12, 0));
        Assert.True(synthetic.IsHalfDay);
        Assert.Equal(4m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void HalfDay_MondayAfternoon_IsFourHours()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(13, 0), timeTo: new TimeOnly(17, 0));
        Assert.True(synthetic.IsHalfDay);
        Assert.Equal(4m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void HalfDay_MondayStartAfterTen_IsHalfDay()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(11, 0), timeTo: new TimeOnly(17, 0));
        Assert.True(synthetic.IsHalfDay);
        Assert.Equal(4m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void HalfDay_Friday_IsThreePointFiveHours()
    {
        var synthetic = SynthesizeSingle(Friday, timeFrom: new TimeOnly(8, 0), timeTo: new TimeOnly(12, 0));
        Assert.Equal(3.5m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void FullDay_MarkedJaWithShortDuration_StaysFullDay()
    {
        var synthetic = SynthesizeSingle(
            Monday,
            timeFrom: new TimeOnly(8, 0),
            timeTo: new TimeOnly(9, 0),
            fullDay: "JA");
        Assert.False(synthetic.IsHalfDay);
        Assert.Equal(8m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void FullDay_NoUsableTimes_DefaultsFullDay()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(8, 0), timeTo: new TimeOnly(8, 0));
        Assert.Equal(8m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void OvernightTimes_AddsTwentyFourHoursBeforeHalfDayHeuristic()
    {
        var duration = LegacyCalendarSynthesis.CalculateDurationHours(
            new TimeOnly(22, 0),
            new TimeOnly(6, 0));
        Assert.Equal(8m, duration);
    }

    [Fact]
    public void HalfDay_MondayEightToThirteen_IsHalfDayBecauseDurationFive()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(8, 0), timeTo: new TimeOnly(13, 0));
        Assert.True(synthetic.IsHalfDay);
        Assert.Equal(4m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void HalfDay_MondayEightToFourteen_IsFullDayBecauseHeuristicFails()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(8, 0), timeTo: new TimeOnly(14, 0));
        Assert.False(synthetic.IsHalfDay);
        Assert.Equal(8m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void HalfDay_MondayTenToSixteen_IsFullDayBecauseStartHourNotGreaterThanTen()
    {
        var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(10, 0), timeTo: new TimeOnly(16, 0));
        Assert.False(synthetic.IsHalfDay);
        Assert.Equal(8m, synthetic.SyntheticHoursRaw);
    }

    [Fact]
    public void Dedupe_RetainsSameCalendarIdDifferentResources()
    {
        var rows = new[]
        {
            SourceRow(idCalendar: 100, idResource: null, resources: "19;22", dateFrom: Monday),
        };
        Assert.Equal(2, LegacyCalendarSynthesis.Synthesize(rows).Count);
    }

    [Fact]
    public void Dedupe_RetainsSameCalendarIdDifferentDates()
    {
        var rows = new[]
        {
            SourceRow(idCalendar: 100, idResource: "19", dateFrom: Monday, dateTo: Monday.AddDays(1)),
        };
        Assert.Equal(2, LegacyCalendarSynthesis.Synthesize(rows).Count);
    }

    [Fact]
    public void DateExpansion_SundayOnly_ProducesZeroRows()
    {
        var sunday = new DateOnly(2026, 7, 12);
        var row = SourceRow(dateFrom: sunday, dateTo: sunday);
        Assert.Empty(LegacyCalendarSynthesis.Synthesize([row]));
    }

    [Fact]
    public void FullDayTextVariants_AllRecognized()
    {
        foreach (var token in new[] { "1", "TRUE", "WAAR", "YES", "JA", "ja", " waar " })
        {
            var synthetic = SynthesizeSingle(Monday, timeFrom: new TimeOnly(8, 0), timeTo: new TimeOnly(12, 0), fullDay: token);
            Assert.False(synthetic.IsHalfDay);
            Assert.Equal(8m, synthetic.SyntheticHoursRaw);
        }
    }

    [Fact]
    public void PowerBiDayOfWeek_MondayIsZero()
    {
        Assert.Equal(0, LegacyCalendarSynthesis.PowerBiDayOfWeek(Monday));
        Assert.Equal(5, LegacyCalendarSynthesis.PowerBiDayOfWeek(Saturday));
        Assert.Equal(6, LegacyCalendarSynthesis.PowerBiDayOfWeek(Saturday.AddDays(1)));
    }

    private static CalendarSyntheticEntry SynthesizeSingle(
        DateOnly date,
        TimeOnly? timeFrom = null,
        TimeOnly? timeTo = null,
        string? fullDay = null) =>
        LegacyCalendarSynthesis.Synthesize(
            [SourceRow(
                dateFrom: date,
                dateTo: date,
                timeFrom: timeFrom,
                timeTo: timeTo,
                fullDay: fullDay)]).Single();

    private static PlenionCalendarRow SourceRow(
        long idCalendar = 1,
        string? idResource = "19",
        string? resources = null,
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null,
        TimeOnly? timeFrom = null,
        TimeOnly? timeTo = null,
        int taskType = 5,
        string? fullDay = null) =>
        new(
            idCalendar,
            idResource,
            resources,
            dateFrom ?? Monday,
            dateTo,
            timeFrom,
            timeTo,
            taskType,
            fullDay,
            "Subject",
            null);
}

using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollModelSmokeTests
{
    [Fact]
    public void NormalizedPerformanceEntry_PreservesExactAtlMinutes()
    {
        const decimal atlHours = 8.83m;
        var entry = new NormalizedPerformanceEntry(
            SourceEntryId: 1,
            SourceEntryKey: "1",
            ResourceId: "1001",
            Date: new DateOnly(2026, 7, 1),
            Start: null,
            End: null,
            AtlHoursRaw: atlHours,
            AtlMinutesExact: atlHours * 60m,
            GrossClockDuration: null,
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: 9,
            ProjectId: null,
            ProjectNumber: null,
            BonNr: null,
            Description: null,
            Memo: null,
            Postcode: null,
            SortKey: 1);

        Assert.Equal(529.8m, entry.AtlMinutesExact);
        Assert.Equal(atlHours, entry.AtlHoursRaw);
    }

    [Fact]
    public void StandardFullTimeSchedule_UsesEightAndSevenHourPattern()
    {
        var schedule = ResourceWorkSchedule.StandardFullTime("ft-default", new DateOnly(2026, 1, 1));

        Assert.Equal(TimeSpan.FromHours(8), schedule.ContractualDuration(DayOfWeek.Monday));
        Assert.Equal(TimeSpan.FromHours(7), schedule.ContractualDuration(DayOfWeek.Friday));
        Assert.Equal(TimeSpan.Zero, schedule.ContractualDuration(DayOfWeek.Saturday));
    }

    [Fact]
    public void DayAndMonthShells_CompileWithNullableComponents()
    {
        var day = new PayrollDayShadowResult
        {
            ResourceId = "1001",
            Date = new DateOnly(2026, 7, 1),
        };
        var month = new PayrollMonthShadowResult
        {
            ResourceId = "1001",
            Year = 2026,
            Month = 7,
        };

        Assert.Null(day.LegacyPayableOrdinaryMinutes);
        Assert.Null(month.Code414Amount);
    }
}

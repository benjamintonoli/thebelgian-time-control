using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

/// <summary>
/// Documents the Ayrton 20/08/2026 live-delete payroll delta:
/// removing an earliest ordinary row can make a later travel (HfdTaak=5) row become
/// daily min VAN, applying travel-begin (-ATL) and Extra15 (+0.25).
/// Net side-effect beyond deleted ATL: -0.0833 h when travel ATL=0.3333.
/// </summary>
public sealed class LegacyTravelMinSideEffectTests
{
    private static readonly DateOnly Day = new(2026, 8, 20);
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    [Fact]
    public void RemovingEarliestWork_BeforeTravelMin_AddsTravelBeginAndExtra15()
    {
        var surviving = new[]
        {
            Row(282047, 8, 50, 9, 10, 0.333333333333333m, hfd: 5, pause: 0m, km: 0m),
            Row(282046, 9, 10, 10, 10, 1m, hfd: 14, pause: 0m, km: 0m),
            Row(282049, 10, 10, 11, 0, 0.833333333333334m, hfd: 5, pause: 0m, km: 0m),
            Row(282048, 11, 0, 17, 0, 5.5m, hfd: 14, pause: 0.5m, km: 9m),
        };
        var withDeleted = surviving
            .Concat([Row(283272, 8, 40, 8, 50, 0.166666666666667m, hfd: 14, pause: 0m, km: null)])
            .OrderBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .ToArray();

        var before = LegacyDailyPayrollPipeline.CalculateDay("388", Day, withDeleted).DailyResult;
        var after = LegacyDailyPayrollPipeline.CalculateDay("388", Day, surviving).DailyResult;

        Assert.Equal(0.166666666666667m, before.RegisteredWorkHours - after.RegisteredWorkHours);
        Assert.Equal(0.333333333333333m, after.TravelStartDeductionHours - before.TravelStartDeductionHours);
        Assert.Equal(0.25m, after.Extra15Hours - before.Extra15Hours);
        Assert.Equal(-0.25m, after.FinalDailyTotalHours - before.FinalDailyTotalHours);
        Assert.Equal(
            -0.083333333333333m,
            (after.FinalDailyTotalHours - before.FinalDailyTotalHours) + 0.166666666666667m,
            precision: 12);
    }

    private static LegacyDailyPerformanceInput Row(
        long id,
        int vanH,
        int vanM,
        int totH,
        int totM,
        decimal atl,
        int hfd,
        decimal pause,
        decimal? km)
    {
        var van = new TimeOnly(vanH, vanM);
        var tot = new TimeOnly(totH, totM);
        return new LegacyDailyPerformanceInput(
            id,
            id,
            hfd,
            new DateTimeOffset(Day.ToDateTime(van), Offset),
            new DateTimeOffset(Day.ToDateTime(tot), Offset),
            atl,
            pause,
            km,
            Day,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

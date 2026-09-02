using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyCurrentYearWindowTests
{
    [Fact]
    public void EvaluationDate_2026_09_01_MapsToCalendarYear2026()
    {
        var window = LegacyCurrentYearWindow.FromEvaluationDate(new DateOnly(2026, 9, 1));
        Assert.Equal(2026, window.Year);
        Assert.Equal(new DateOnly(2026, 1, 1), window.FirstDay);
        Assert.Equal(new DateOnly(2026, 12, 31), window.LastDay);
        Assert.Equal(new DateOnly(2026, 9, 1), window.EvaluationDate);
    }

    [Fact]
    public void EvaluationDate_2027_03_15_MapsToCalendarYear2027()
    {
        var window = LegacyCurrentYearWindow.FromEvaluationDate(new DateOnly(2027, 3, 15));
        Assert.Equal(2027, window.Year);
        Assert.Equal(new DateOnly(2027, 1, 1), window.FirstDay);
        Assert.Equal(new DateOnly(2027, 12, 31), window.LastDay);
    }

    [Fact]
    public void CjWindow_IncludesFirstDayAndEvaluationDate_ExcludesAfter()
    {
        var window = LegacyCurrentYearWindow.FromEvaluationDate(new DateOnly(2026, 9, 1));
        Assert.True(window.IsInCalculationWindow(new DateOnly(2026, 1, 1)));
        Assert.True(window.IsInCalculationWindow(new DateOnly(2026, 9, 1)));
        Assert.False(window.IsInCalculationWindow(new DateOnly(2025, 12, 31)));
        Assert.False(window.IsInCalculationWindow(new DateOnly(2026, 9, 2)));
        Assert.False(window.IsInCalculationWindow(new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void FromPeriod_UsesEvaluationDateYear_NotPayrollMonth()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 9, 1));
        var window = LegacyCurrentYearWindow.FromPeriod(period);
        Assert.Equal(2026, window.Year);
        Assert.Equal(new DateOnly(2026, 1, 1), window.FirstDay);
        Assert.Equal(new DateOnly(2026, 9, 1), window.EvaluationDate);
    }
}

public sealed class LegacyKmEffectiveDateRangeTests
{
    [Fact]
    public void JulyPeriod_WithSeptemberEvaluation_IntersectsToJulyOnly()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 9, 1));
        var window = LegacyCurrentYearWindow.FromPeriod(period);
        var range = LegacyKmEffectiveDateRange.Intersect(period, window);

        Assert.NotNull(range);
        Assert.Equal(new DateOnly(2026, 7, 1), range!.Start);
        Assert.Equal(new DateOnly(2026, 7, 31), range.End);
    }

    [Fact]
    public void OpenAugust_TruncatesAtEvaluationDate()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 8, new DateOnly(2026, 8, 15));
        var window = LegacyCurrentYearWindow.FromPeriod(period);
        var range = LegacyKmEffectiveDateRange.Intersect(period, window);

        Assert.NotNull(range);
        Assert.Equal(new DateOnly(2026, 8, 1), range!.Start);
        Assert.Equal(new DateOnly(2026, 8, 15), range.End);
    }

    [Fact]
    public void YearBoundary_ExcludesDecemberByCjWindow()
    {
        var period = new PayrollPeriodSnapshot(
            2026,
            1,
            new DateOnly(2025, 12, 15),
            new DateOnly(2026, 1, 15),
            new DateOnly(2026, 1, 15));
        var window = LegacyCurrentYearWindow.FromPeriod(period);
        var range = LegacyKmEffectiveDateRange.Intersect(period, window);

        Assert.NotNull(range);
        Assert.Equal(new DateOnly(2026, 1, 1), range!.Start);
        Assert.Equal(new DateOnly(2026, 1, 15), range.End);
    }
}

public sealed class LegacyKmAllowanceCalculatorTests
{
    private static readonly KmAllowanceConfiguration Rate =
        KmAllowanceConfiguration.Year2026Legacy;

    private static readonly PayrollPeriodSnapshot JulyPeriod =
        PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 9, 1));

    [Fact]
    public void JulyPeriod_ExcludesJanJunAug_KeepsJulyOnly()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 1, 15), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 6, 15), hfd: 9, km: 200m),
            Row(3, new DateOnly(2026, 7, 15), hfd: 9, km: 300m),
            Row(4, new DateOnly(2026, 8, 15), hfd: 9, km: 400m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(300m, result.EligibleKm);
    }

    [Fact]
    public void OpenMonth_OnlyThroughEvaluationDate()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 8, new DateOnly(2026, 8, 15));
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 31), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 8, 1), hfd: 9, km: 10m),
            Row(3, new DateOnly(2026, 8, 15), hfd: 9, km: 20m),
            Row(4, new DateOnly(2026, 8, 16), hfd: 9, km: 100m),
            Row(5, new DateOnly(2026, 1, 1), hfd: 9, km: 500m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, period, Rate);
        Assert.Equal(30m, result.EligibleKm);
    }

    [Fact]
    public void WholeYearContext_UsesCjThroughEvaluationDate()
    {
        var period = new PayrollPeriodSnapshot(
            2026,
            1,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            new DateOnly(2026, 9, 1));
        var rows = new[]
        {
            Row(1, new DateOnly(2025, 12, 31), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 1, 1), hfd: 9, km: 10m),
            Row(3, new DateOnly(2026, 9, 1), hfd: 9, km: 20m),
            Row(4, new DateOnly(2026, 9, 2), hfd: 9, km: 100m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, period, Rate);
        Assert.Equal(30m, result.EligibleKm);
    }

    [Fact]
    public void YearBoundaryPeriod_ExcludesDecemberByCj()
    {
        var period = new PayrollPeriodSnapshot(
            2026,
            1,
            new DateOnly(2025, 12, 15),
            new DateOnly(2026, 1, 15),
            new DateOnly(2026, 1, 15));
        var rows = new[]
        {
            Row(1, new DateOnly(2025, 12, 20), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 1, 1), hfd: 9, km: 10m),
            Row(3, new DateOnly(2026, 1, 15), hfd: 9, km: 20m),
            Row(4, new DateOnly(2026, 1, 16), hfd: 9, km: 100m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, period, Rate);
        Assert.Equal(30m, result.EligibleKm);
    }

    [Fact]
    public void Extra75_JulyPeriod_UsesJulyOnly_NotJuneOrAugust()
    {
        // Single-row days: Extra75 = KM - 150 when KM > 150 and both min+max
        // June 210 → Extra75 60; July 270 → 120; August 330 → 180
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 6, 15), hfd: 9, km: 210m, vanHours: 8m),
            Row(2, new DateOnly(2026, 7, 15), hfd: 9, km: 270m, vanHours: 8m),
            Row(3, new DateOnly(2026, 8, 15), hfd: 9, km: 330m, vanHours: 8m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(120m, result.Extra75RawKm);
        Assert.Equal(2m, result.Extra75YtdHours);
    }

    [Fact]
    public void Extra75_JulyPeriod_IncludesTask5InsideJuly()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 5, km: 200m, vanHours: 7m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(0m, result.EligibleKm);
        Assert.Equal(50m, result.Extra75RawKm);
        Assert.Equal(50m / 60m, result.Extra75YtdHours);
    }

    [Fact]
    public void EligibleKm_IncludesNormalRow_ExcludesTask5()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 7, 1), hfd: 5, km: 50m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(100m, result.EligibleKm);
    }

    [Fact]
    public void EligibleKm_IncludesStandbyTask23_WithKm()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 23, km: 12m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(12m, result.EligibleKm);
    }

    [Fact]
    public void EligibleKm_NullKm_CountsAsZero()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: null),
            Row(2, new DateOnly(2026, 7, 2), hfd: 9, km: 10m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(10m, result.EligibleKm);
    }

    [Fact]
    public void EligibleKm_SumsMultipleJulyRowsExactly()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 100.5m),
            Row(2, new DateOnly(2026, 7, 2), hfd: 9, km: 200.25m),
            Row(3, new DateOnly(2026, 7, 3), hfd: 14, km: 50m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(350.75m, result.EligibleKm);
    }

    [Fact]
    public void Extra75Ytd_DividesRawSumBy60()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 214m, vanHours: 8m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(64m, result.Extra75RawKm);
        Assert.Equal(64m / 60m, result.Extra75YtdHours);
    }

    [Fact]
    public void Extra75Ytd_ZeroWhenNoExtra75()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 50m, vanHours: 8m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(0m, result.Extra75RawKm);
        Assert.Equal(0m, result.Extra75YtdHours);
    }

    [Fact]
    public void KmAmount_PreservesDimensionalOddity()
    {
        var eligible = 941m;
        var extra75Ytd = 64m / 60m;
        var expected = 0.1448m * (eligible - extra75Ytd);

        var rows = new List<LegacyDailyPerformanceInput>
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 50m, vanHours: 7m),
            Row(2, new DateOnly(2026, 7, 1), hfd: 9, km: 214m, vanHours: 8m),
            Row(3, new DateOnly(2026, 7, 1), hfd: 9, km: 50m, vanHours: 16m),
            Row(4, new DateOnly(2026, 7, 2), hfd: 9, km: 214m, vanHours: 8m),
            Row(5, new DateOnly(2026, 7, 3), hfd: 9, km: 0m, vanHours: 7m),
            Row(6, new DateOnly(2026, 7, 3), hfd: 9, km: 413m, vanHours: 8m),
            Row(7, new DateOnly(2026, 7, 3), hfd: 9, km: 0m, vanHours: 16m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(941m, result.EligibleKm);
        Assert.Equal(64m, result.Extra75RawKm);
        Assert.Equal(extra75Ytd, result.Extra75YtdHours);
        Assert.Equal(expected, result.KmAmount);
        Assert.Equal(0.1448m, result.RatePerKm);
    }

    [Fact]
    public void KmAmount_ZeroWhenNoKm()
    {
        var result = LegacyKmAllowanceCalculator.Calculate([], JulyPeriod, Rate);
        Assert.Equal(0m, result.EligibleKm);
        Assert.Equal(0m, result.KmAmount);
    }

    [Fact]
    public void KmAmount_AllowsNegativeNetQuantity()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 5, km: 200m, vanHours: 7m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, Rate);
        Assert.Equal(0m, result.EligibleKm);
        Assert.True(result.NetKmLegacyQuantity < 0m);
        Assert.True(result.KmAmount < 0m);
        Assert.Equal(0.1448m * result.NetKmLegacyQuantity, result.KmAmount);
    }

    [Fact]
    public void KmAmount_UsesInjectedRate()
    {
        var config = new KmAllowanceConfiguration(new DateOnly(2026, 1, 1), null, 0.2m);
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 0m, vanHours: 7m),
            Row(2, new DateOnly(2026, 7, 1), hfd: 9, km: 100m, vanHours: 8m),
            Row(3, new DateOnly(2026, 7, 1), hfd: 9, km: 0m, vanHours: 16m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, JulyPeriod, config);
        Assert.Equal(0.2m, result.RatePerKm);
        Assert.Equal(0m, result.Extra75YtdHours);
        Assert.Equal(0.2m * 100m, result.KmAmount);
    }

    private static LegacyDailyPerformanceInput Row(
        long id,
        DateOnly date,
        int hfd,
        decimal? km,
        decimal vanHours = 8m) =>
        new(
            id,
            id,
            hfd,
            new DateTimeOffset(date.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours((double)vanHours))), TimeSpan.Zero),
            null,
            1m,
            0m,
            km,
            date,
            id.ToString(CultureInfo.InvariantCulture));
}

public sealed class LegacyCode414ShadowCalculatorTests
{
    [Fact]
    public void BothCalculated_SumsCityAndKm()
    {
        var (status, amount) = LegacyCode414ShadowCalculator.Calculate(
            PayrollMonthCalculationStatus.Calculated,
            45m,
            PayrollMonthCalculationStatus.Calculated,
            136.1023m);
        Assert.Equal(PayrollMonthCalculationStatus.Calculated, status);
        Assert.Equal(181.1023m, amount);
    }

    [Fact]
    public void CityZero_ReturnsKmOnly()
    {
        var (status, amount) = LegacyCode414ShadowCalculator.Calculate(
            PayrollMonthCalculationStatus.Calculated,
            0m,
            PayrollMonthCalculationStatus.Calculated,
            10m);
        Assert.Equal(PayrollMonthCalculationStatus.Calculated, status);
        Assert.Equal(10m, amount);
    }

    [Fact]
    public void KmZero_ReturnsCityOnly()
    {
        var (status, amount) = LegacyCode414ShadowCalculator.Calculate(
            PayrollMonthCalculationStatus.Calculated,
            25m,
            PayrollMonthCalculationStatus.Calculated,
            0m);
        Assert.Equal(PayrollMonthCalculationStatus.Calculated, status);
        Assert.Equal(25m, amount);
    }

    [Fact]
    public void CityNotCalculated_ReturnsNull()
    {
        var (status, amount) = LegacyCode414ShadowCalculator.Calculate(
            PayrollMonthCalculationStatus.NotCalculated,
            null,
            PayrollMonthCalculationStatus.Calculated,
            10m);
        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, status);
        Assert.Null(amount);
    }

    [Fact]
    public void KmNotCalculated_ReturnsNull()
    {
        var (status, amount) = LegacyCode414ShadowCalculator.Calculate(
            PayrollMonthCalculationStatus.Calculated,
            25m,
            PayrollMonthCalculationStatus.NotCalculated,
            null);
        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, status);
        Assert.Null(amount);
    }
}

public sealed class KmAllowanceConfigurationTests
{
    [Fact]
    public void Year2026Legacy_IsActiveThrough2026()
    {
        var config = KmAllowanceConfiguration.Year2026Legacy;
        Assert.Equal(0.1448m, config.RatePerKm);
        Assert.True(config.IsActiveOn(new DateOnly(2026, 1, 1)));
        Assert.True(config.IsActiveOn(new DateOnly(2026, 9, 1)));
        Assert.False(config.IsActiveOn(new DateOnly(2027, 1, 1)));
    }
}

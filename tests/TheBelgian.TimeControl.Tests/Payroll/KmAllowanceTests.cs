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
    public void CalculationWindow_IncludesFirstDayAndEvaluationDate_ExcludesAfter()
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

public sealed class LegacyKmAllowanceCalculatorTests
{
    private static readonly LegacyCurrentYearWindow Window =
        LegacyCurrentYearWindow.FromEvaluationDate(new DateOnly(2026, 9, 1));

    private static readonly KmAllowanceConfiguration Rate =
        KmAllowanceConfiguration.Year2026Legacy;

    [Fact]
    public void EligibleKm_IncludesNormalRow_ExcludesTask5()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 7, 1), hfd: 5, km: 50m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(100m, result.EligibleKm);
    }

    [Fact]
    public void EligibleKm_IncludesStandbyTask23_WithKm()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 23, km: 12m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
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

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(10m, result.EligibleKm);
    }

    [Fact]
    public void EligibleKm_ExcludesBeforeFirstDay_AndAfterEvaluationDate()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2025, 12, 31), hfd: 9, km: 100m),
            Row(2, new DateOnly(2026, 1, 1), hfd: 9, km: 10m),
            Row(3, new DateOnly(2026, 9, 1), hfd: 9, km: 20m),
            Row(4, new DateOnly(2026, 9, 2), hfd: 9, km: 100m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(30m, result.EligibleKm);
    }

    [Fact]
    public void EligibleKm_SumsMultipleRowsExactly()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 2, 1), hfd: 9, km: 100.5m),
            Row(2, new DateOnly(2026, 3, 1), hfd: 9, km: 200.25m),
            Row(3, new DateOnly(2026, 4, 1), hfd: 14, km: 50m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(350.75m, result.EligibleKm);
    }

    [Fact]
    public void Extra75Ytd_DividesRawSumBy60()
    {
        // Single row that is both min and max VAN with KM > 150 → Extra75 = KM - 150
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 214m, vanHours: 8m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
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

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(0m, result.Extra75RawKm);
        Assert.Equal(0m, result.Extra75YtdHours);
    }

    [Fact]
    public void Extra75Ytd_IncludesTask5WhenSyntheticNonzeroExtra75()
    {
        // Task 5 alone is both min and max; Extra75 YTD measure has no task5 exclusion.
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 5, km: 200m, vanHours: 7m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(0m, result.EligibleKm); // task 5 excluded from Aantal KM
        Assert.Equal(50m, result.Extra75RawKm); // 200 - 150 (both min+max)
        Assert.Equal(50m / 60m, result.Extra75YtdHours);
    }

    [Fact]
    public void Extra75Ytd_ExcludesOutsideCalculationWindow()
    {
        var rows = new[]
        {
            Row(1, new DateOnly(2025, 12, 31), hfd: 9, km: 214m, vanHours: 8m),
            Row(2, new DateOnly(2026, 9, 2), hfd: 9, km: 214m, vanHours: 8m),
            Row(3, new DateOnly(2026, 7, 1), hfd: 9, km: 214m, vanHours: 8m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(64m, result.Extra75RawKm);
    }

    [Fact]
    public void KmAmount_PreservesDimensionalOddity()
    {
        // Eligible 941, Extra75Ytd 1.0666... → amount = 0.1448 * (941 - 1.0666...)
        var eligible = 941m;
        var extra75Ytd = 64m / 60m;
        var expected = 0.1448m * (eligible - extra75Ytd);

        // Construct via calculator: one day both min/max with KM=214 → Extra75=64;
        // remaining eligible filled with middle-of-day rows that aren't min/max.
        var rows = new List<LegacyDailyPerformanceInput>
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 214m, vanHours: 8m),
        };
        // Add 727 km on separate days as neither-only... wait, single row days are always both min+max.
        // So use multi-row days where only mid rows contribute KM without Extra75.
        rows.Clear();
        rows.Add(Row(1, new DateOnly(2026, 7, 1), hfd: 9, km: 50m, vanHours: 7m)); // min, km<=75 → Extra75=0
        rows.Add(Row(2, new DateOnly(2026, 7, 1), hfd: 9, km: 214m, vanHours: 8m)); // neither → Extra75=0
        rows.Add(Row(3, new DateOnly(2026, 7, 1), hfd: 9, km: 50m, vanHours: 16m)); // max, km<=75 → Extra75=0
        // Need Extra75 = 64: use a day with single row KM=214
        rows.Add(Row(4, new DateOnly(2026, 7, 2), hfd: 9, km: 214m, vanHours: 8m));
        // Remaining eligible: 50+214+50+214 = 528; need 941 → add 413 on mid-day only
        rows.Add(Row(5, new DateOnly(2026, 7, 3), hfd: 9, km: 0m, vanHours: 7m));
        rows.Add(Row(6, new DateOnly(2026, 7, 3), hfd: 9, km: 413m, vanHours: 8m));
        rows.Add(Row(7, new DateOnly(2026, 7, 3), hfd: 9, km: 0m, vanHours: 16m));

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
        Assert.Equal(941m, result.EligibleKm);
        Assert.Equal(64m, result.Extra75RawKm);
        Assert.Equal(extra75Ytd, result.Extra75YtdHours);
        Assert.Equal(expected, result.KmAmount);
        Assert.Equal(0.1448m, result.RatePerKm);
    }

    [Fact]
    public void KmAmount_ZeroWhenNoKm()
    {
        var result = LegacyKmAllowanceCalculator.Calculate([], Window, Rate);
        Assert.Equal(0m, result.EligibleKm);
        Assert.Equal(0m, result.KmAmount);
    }

    [Fact]
    public void KmAmount_AllowsNegativeNetQuantity()
    {
        // Eligible 0 (only task5), Extra75Ytd > 0 from task5 → negative amount
        var rows = new[]
        {
            Row(1, new DateOnly(2026, 7, 1), hfd: 5, km: 200m, vanHours: 7m),
        };

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, Rate);
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

        var result = LegacyKmAllowanceCalculator.Calculate(rows, Window, config);
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

using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyTravelDerivationTests
{
    [Fact]
    public void TravelAtMinAndMax_ProducesStartEndAndSingleExtra15()
    {
        var rows = new[]
        {
            TravelInput(1, 5, TimeSpan.FromHours(7), 0.5m),
            TravelInput(2, 9, TimeSpan.FromHours(8), 4m),
            TravelInput(3, 5, TimeSpan.FromHours(16), 0.75m),
        };

        var results = LegacyTravelDerivation.CalculateRows(rows);
        var day = LegacyTravelDerivation.CalculateDay("633", new DateOnly(2026, 7, 1), rows);

        Assert.Equal(0.5m, day.TravelStartDeductionHours);
        Assert.Equal(0.75m, day.TravelEndDeductionHours);
        Assert.Equal(0.5m, day.Extra15TotalHours);
        Assert.Equal(0.25m, results.Single(result => result.PerformanceId == 1).Extra15Hours);
        Assert.Equal(0.25m, results.Single(result => result.PerformanceId == 3).Extra15Hours);
    }

    [Fact]
    public void SingleTravelRow_IsBothMinAndMax_Extra15IsQuarterHourNotHalf()
    {
        var rows = new[] { TravelInput(1, 5, TimeSpan.FromHours(8), 1m) };
        var day = LegacyTravelDerivation.CalculateDay("633", new DateOnly(2026, 7, 1), rows);
        var row = LegacyTravelDerivation.CalculateRows(rows).Single();

        Assert.Equal(1m, day.TravelStartDeductionHours);
        Assert.Equal(1m, day.TravelEndDeductionHours);
        Assert.Equal(0.25m, day.Extra15TotalHours);
        Assert.Equal(0.25m, row.Extra15Hours);
    }

    [Fact]
    public void MultipleMinTies_EachQualifyingTravelRowGetsExtra15()
    {
        var rows = new[]
        {
            TravelInput(1, 5, TimeSpan.FromHours(7), 0.5m),
            TravelInput(2, 5, TimeSpan.FromHours(7), 0.33m),
            TravelInput(3, 9, TimeSpan.FromHours(8), 4m),
        };

        var day = LegacyTravelDerivation.CalculateDay("656", new DateOnly(2026, 7, 1), rows);
        Assert.Equal(0.5m, day.TravelStartDeductionHours);
        Assert.Equal(0.5m, day.Extra15TotalHours);
    }

    [Fact]
    public void WorkRowAtMinVan_DoesNotProduceTravelDeduction()
    {
        var rows = new[]
        {
            TravelInput(1, 9, TimeSpan.FromHours(7), 8m),
            TravelInput(2, 5, TimeSpan.FromHours(16), 0.5m),
        };

        var day = LegacyTravelDerivation.CalculateDay("495", new DateOnly(2026, 7, 1), rows);
        Assert.Equal(0m, day.TravelStartDeductionHours);
        Assert.Equal(0.5m, day.TravelEndDeductionHours);
    }

    private static LegacyTravelPerformanceInput TravelInput(
        long id,
        int hfdTaak,
        TimeSpan van,
        decimal atl) =>
        new(id, hfdTaak, van, atl);
}

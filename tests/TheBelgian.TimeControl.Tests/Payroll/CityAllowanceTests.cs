using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PostcodeNormalizerTests
{
    [Theory]
    [InlineData("1000", "1000")]
    [InlineData(" 1020 ", "1020")]
    [InlineData("9999", "9999")]
    public void ValidPostcodes_Normalize(string raw, string expected) =>
        Assert.Equal(expected, PostcodeNormalizer.TryNormalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC")]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("0999")]
    public void InvalidPostcodes_ReturnNull(string? raw) =>
        Assert.Null(PostcodeNormalizer.TryNormalize(raw));
}

public sealed class PlenionPostcodeResolverPrecedenceTests
{
    private static readonly IReadOnlyDictionary<string, string> BonPostcodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["15502702"] = "1800",
        };

    private static readonly IReadOnlyDictionary<string, PlenionPostcodeResolver.ProjectPostcodeFallback> ProjectFallbacks =
        new Dictionary<string, PlenionPostcodeResolver.ProjectPostcodeFallback>(StringComparer.Ordinal)
        {
            ["42"] = new("2000", "3000"),
            ["43"] = new(null, "2050"),
            ["44"] = new("  1040  ", null),
        };

    [Fact]
    public void ValidBonAndProject_BonWins()
    {
        var row = Row(bonNr: "15502702", projectId: "42");
        var result = PlenionPostcodeResolver.ResolveRow(row, BonPostcodes, ProjectFallbacks);
        Assert.Equal("1800", result.Postcode);
        Assert.Equal(PostcodeResolutionSource.BonDeliveryAddress, result.Source);
    }

    [Fact]
    public void MissingBon_UsesProjectPostalCode()
    {
        var row = Row(bonNr: null, projectId: "42");
        var result = PlenionPostcodeResolver.ResolveRow(row, BonPostcodes, ProjectFallbacks);
        Assert.Equal("2000", result.Postcode);
        Assert.Equal(PostcodeResolutionSource.ProjectPostalCode, result.Source);
    }

    [Fact]
    public void InvalidBon_UsesProjectPostalCode()
    {
        var row = Row(bonNr: "missing", projectId: "44");
        var result = PlenionPostcodeResolver.ResolveRow(row, BonPostcodes, ProjectFallbacks);
        Assert.Equal("1040", result.Postcode);
        Assert.Equal(PostcodeResolutionSource.ProjectPostalCode, result.Source);
    }

    [Fact]
    public void MissingBonAndProjectPostal_UsesProjectDeliveryAddress()
    {
        var row = Row(bonNr: null, projectId: "43");
        var result = PlenionPostcodeResolver.ResolveRow(row, BonPostcodes, ProjectFallbacks);
        Assert.Equal("2050", result.Postcode);
        Assert.Equal(PostcodeResolutionSource.ProjectDeliveryAddress, result.Source);
    }

    [Fact]
    public void AllMissing_ReturnsUnresolved()
    {
        var row = Row(bonNr: null, projectId: "999");
        var result = PlenionPostcodeResolver.ResolveRow(row, BonPostcodes, ProjectFallbacks);
        Assert.Equal(PostcodeResolutionSource.Unresolved, result.Source);
        Assert.Null(result.Postcode);
    }

    [Fact]
    public void InvalidNonNumericBon_FallsBackOrUnresolved()
    {
        var bonMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bad"] = "ABCD",
        };
        var row = Row(bonNr: "bad", projectId: "42");
        var result = PlenionPostcodeResolver.ResolveRow(row, bonMap, ProjectFallbacks);
        Assert.Equal("2000", result.Postcode);
        Assert.Equal(PostcodeResolutionSource.ProjectPostalCode, result.Source);
    }

    private static PlenionPayrollPerformanceRow Row(string? bonNr, string? projectId) =>
        new(
            1,
            new DateOnly(2026, 7, 1),
            null,
            null,
            null,
            1m,
            null,
            "495",
            projectId,
            9,
            bonNr,
            null,
            null,
            null,
            null);
}

public sealed class LegacyCityAllowanceRowCalculatorTests
{
    private static readonly CityAllowanceConfiguration Config =
        CityAllowanceConfiguration.July2026Legacy;

    [Theory]
    [InlineData("1000", true, false, 1)]
    [InlineData("1000", false, true, 1)]
    [InlineData("1000", true, true, 2)]
    [InlineData("1000", false, false, 0)]
    [InlineData("9000", true, true, 0)]
    [InlineData(null, true, true, 0)]
    public void RowUnits_FollowLegacyRule(
        string? postcode,
        bool isMin,
        bool isMax,
        int expected) =>
        Assert.Equal(
            expected,
            LegacyCityAllowanceRowCalculator.CalculateRowUnits(postcode, isMin, isMax, Config));

    [Fact]
    public void CityBothMinMax_GivesTwoUnits_WhileTravelExtra15StaysQuarterHour()
    {
        var rows = new[]
        {
            new LegacyTravelPerformanceInput(1, 5, TimeSpan.FromHours(7), 0.5m),
            new LegacyTravelPerformanceInput(2, 9, TimeSpan.FromHours(8), 4m),
            new LegacyTravelPerformanceInput(3, 5, TimeSpan.FromHours(16), 0.75m),
        };

        var travelResults = LegacyTravelDerivation.CalculateRows(rows);
        var minTravel = travelResults.Single(result => result.PerformanceId == 1);
        var maxTravel = travelResults.Single(result => result.PerformanceId == 3);

        Assert.Equal(0.25m, minTravel.Extra15Hours);
        Assert.Equal(0.25m, maxTravel.Extra15Hours);
        Assert.Equal(
            1,
            LegacyCityAllowanceRowCalculator.CalculateRowUnits("1000", minTravel.IsDailyMinVan, minTravel.IsDailyMaxVan, Config));
        Assert.Equal(
            1,
            LegacyCityAllowanceRowCalculator.CalculateRowUnits("1000", maxTravel.IsDailyMinVan, maxTravel.IsDailyMaxVan, Config));
        Assert.Equal(
            2,
            LegacyCityAllowanceRowCalculator.CalculateRowUnits(
                "1000",
                true,
                true,
                Config));
    }
}

public sealed class LegacyCityAllowanceMonthlyCalculatorTests
{
    [Fact]
    public void MonthlyAmount_IsUnitsTimesTripAmount()
    {
        var result = LegacyCityAllowanceMonthlyCalculator.Calculate(
            9,
            CityAllowanceConfiguration.July2026Legacy);
        Assert.Equal(9, result.CityTripUnits);
        Assert.Equal(45m, result.CityAllowanceAmount);
    }
}

public sealed class CityAllowanceConfigurationTests
{
    [Fact]
    public void July2026Legacy_IsActiveForJulyOnly()
    {
        var config = CityAllowanceConfiguration.July2026Legacy;
        Assert.True(config.IsActiveOn(new DateOnly(2026, 7, 1)));
        Assert.True(config.IsActiveOn(new DateOnly(2026, 7, 31)));
        Assert.False(config.IsActiveOn(new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void QualifyingPostcodes_Include9999()
    {
        var config = CityAllowanceConfiguration.July2026Legacy;
        Assert.True(config.IsQualifyingPostcode("9999"));
        Assert.False(config.IsQualifyingPostcode("9000"));
    }
}

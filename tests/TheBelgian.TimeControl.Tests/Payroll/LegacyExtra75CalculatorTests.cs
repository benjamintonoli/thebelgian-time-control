using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyExtra75CalculatorTests
{
    [Fact]
    public void NullOrZeroKm_ReturnsZero()
    {
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(null, true, true));
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(0m, true, true));
    }

    [Fact]
    public void Km75_ReturnsZero()
    {
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(75m, true, false));
    }

    [Fact]
    public void Km75Point01_MinOnly_ReturnsPoint01()
    {
        Assert.Equal(0.01m, LegacyExtra75Calculator.CalculateRowKm(75.01m, true, false));
    }

    [Fact]
    public void KmAbove75_MaxOnly_ReturnsDifference()
    {
        Assert.Equal(25m, LegacyExtra75Calculator.CalculateRowKm(100m, false, true));
    }

    [Fact]
    public void KmAbove75_NeitherFlag_ReturnsZero()
    {
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(100m, false, false));
    }

    [Fact]
    public void Km149_BothFlags_ReturnsZeroFromSecondBranch()
    {
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(149m, true, true));
    }

    [Fact]
    public void Km150_BothFlags_Returns75FromThirdBranch()
    {
        Assert.Equal(75m, LegacyExtra75Calculator.CalculateRowKm(150m, true, true));
    }

    [Fact]
    public void Km149Point99_BothFlags_ReturnsZeroFromSecondBranch()
    {
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(149.99m, true, true));
    }

    [Fact]
    public void Km150Point01_BothFlags_ReturnsPoint01FromFirstBranch()
    {
        Assert.Equal(0.01m, LegacyExtra75Calculator.CalculateRowKm(150.01m, true, true));
    }

    [Fact]
    public void Km151_BothFlags_ReturnsOneFromFirstBranch()
    {
        Assert.Equal(1m, LegacyExtra75Calculator.CalculateRowKm(151m, true, true));
    }

    [Fact]
    public void Km160_MinOnly_Returns85()
    {
        Assert.Equal(85m, LegacyExtra75Calculator.CalculateRowKm(160m, true, false));
    }

    [Fact]
    public void Km160_MaxOnly_Returns85()
    {
        Assert.Equal(85m, LegacyExtra75Calculator.CalculateRowKm(160m, false, true));
    }

    [Fact]
    public void Km160_Neither_ReturnsZero()
    {
        Assert.Equal(0m, LegacyExtra75Calculator.CalculateRowKm(160m, false, false));
    }
}

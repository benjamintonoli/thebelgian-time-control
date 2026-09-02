using TheBelgian.TimeControl.Core.Configuration;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollShadowOptionsTests
{
    [Fact]
    public void Defaults_AreDisabled()
    {
        var options = new PayrollShadowOptions();
        Assert.False(options.Enabled);
        Assert.False(options.AdminUiEnabled);
    }

    [Fact]
    public void AdminUiWithoutEnabled_IsInvalid()
    {
        var options = new PayrollShadowOptions { AdminUiEnabled = true };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }
}

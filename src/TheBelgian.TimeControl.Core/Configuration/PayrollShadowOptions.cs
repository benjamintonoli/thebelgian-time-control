namespace TheBelgian.TimeControl.Core.Configuration;

public sealed class PayrollShadowOptions
{
    public const string SectionName = "PayrollShadow";

    public bool Enabled { get; set; }

    public bool AdminUiEnabled { get; set; }

    public void Validate()
    {
        if (AdminUiEnabled && !Enabled)
        {
            throw new InvalidOperationException(
                "PayrollShadow:AdminUiEnabled vereist PayrollShadow:Enabled=true.");
        }
    }
}

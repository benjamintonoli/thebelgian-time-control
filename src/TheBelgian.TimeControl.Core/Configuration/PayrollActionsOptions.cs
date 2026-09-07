namespace TheBelgian.TimeControl.Core.Configuration;

public sealed class PayrollActionsOptions
{
    public const string SectionName = "PayrollActions";

    public bool Enabled { get; set; }

    /// <summary>
    /// When false, proposals remain visible for review but execution is disabled.
    /// </summary>
    public bool ExecutionEnabled { get; set; }

    public void Validate()
    {
        if (ExecutionEnabled && !Enabled)
        {
            throw new InvalidOperationException(
                "PayrollActions:ExecutionEnabled vereist PayrollActions:Enabled=true.");
        }
    }
}

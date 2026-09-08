namespace TheBelgian.TimeControl.Core.Configuration;

public sealed class PayrollActionsOptions
{
    public const string SectionName = "PayrollActions";

    public bool Enabled { get; set; }

    /// <summary>
    /// When false, proposals remain visible for review but execution is disabled.
    /// </summary>
    public bool ExecutionEnabled { get; set; }

    /// <summary>When true (and ExecutionEnabled), adjust-time actions may execute.</summary>
    public bool AdjustTimeEnabled { get; set; }

    /// <summary>When true (and ExecutionEnabled), delete-performance actions may execute.</summary>
    public bool DeletePerformanceEnabled { get; set; }

    /// <summary>When true (and ExecutionEnabled), create-performance actions may execute. Keep false until create contract proven.</summary>
    public bool CreatePerformanceEnabled { get; set; }

    public void Validate()
    {
        if (ExecutionEnabled && !Enabled)
        {
            throw new InvalidOperationException(
                "PayrollActions:ExecutionEnabled vereist PayrollActions:Enabled=true.");
        }
    }
}

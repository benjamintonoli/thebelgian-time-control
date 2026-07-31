namespace TheBelgian.TimeControl.Core.Configuration;

public static class ReviewDataModes
{
    public const string Offline = "Offline";
    public const string LivePilot = "LivePilot";
}

/// <summary>
/// Admin review data source. Default remains Offline. LivePilot requires explicit config.
/// </summary>
public sealed class ReviewDataOptions
{
    public const string SectionName = "ReviewData";

    /// <summary>Offline (default) or LivePilot.</summary>
    public string Mode { get; set; } = ReviewDataModes.Offline;

    /// <summary>Plenion resource id for the single live-pilot technician.</summary>
    public string? TechnicianResourceId { get; set; }

    /// <summary>Optional Powerfleet driver id when already known to be reliable.</summary>
    public string? PowerfleetDriverId { get; set; }

    public DateOnly? DateFrom { get; set; }

    public DateOnly? DateTo { get; set; }

    /// <summary>Maximum inclusive calendar days for LivePilot (default 5).</summary>
    public int MaxDays { get; set; } = 5;

    /// <summary>Must remain false. Live pilot is read-only.</summary>
    public bool AllowWriteback { get; set; }

    /// <summary>Must remain false. No automatic corrections.</summary>
    public bool AllowAutomaticCorrections { get; set; }

    public bool IsOffline =>
        string.Equals(Mode, ReviewDataModes.Offline, StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(Mode);

    public bool IsLivePilot =>
        string.Equals(Mode, ReviewDataModes.LivePilot, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (IsOffline)
        {
            return;
        }

        if (!IsLivePilot)
        {
            throw new InvalidOperationException(
                $"ReviewData:Mode '{Mode}' is ongeldig. Gebruik Offline of LivePilot.");
        }

        if (string.IsNullOrWhiteSpace(TechnicianResourceId))
        {
            throw new InvalidOperationException(
                "LivePilot vereist ReviewData:TechnicianResourceId.");
        }

        if (DateFrom is null || DateTo is null)
        {
            throw new InvalidOperationException(
                "LivePilot vereist ReviewData:DateFrom en ReviewData:DateTo.");
        }

        if (DateTo.Value < DateFrom.Value)
        {
            throw new InvalidOperationException(
                "LivePilot: DateTo ligt vóór DateFrom.");
        }

        var maxDays = MaxDays <= 0 ? 5 : MaxDays;
        if (maxDays > 5)
        {
            throw new InvalidOperationException(
                "LivePilot: MaxDays mag maximaal 5 zijn.");
        }

        var calendarDays = DateTo.Value.DayNumber - DateFrom.Value.DayNumber + 1;
        if (calendarDays > maxDays)
        {
            throw new InvalidOperationException(
                $"LivePilot-periode is {calendarDays} kalenderdagen; maximaal {maxDays} toegestaan.");
        }

        if (AllowWriteback || AllowAutomaticCorrections)
        {
            throw new InvalidOperationException(
                "LivePilot weigert te starten wanneer writeback of automatische correctie is geconfigureerd.");
        }
    }
}

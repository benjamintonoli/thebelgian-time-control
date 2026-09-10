namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// User-facing primary timing source for Create proposals.
/// Separate from supporting evidence and from EvidenceClass.
/// </summary>
public enum PayrollPrimaryTimingSource
{
    OwnGps = 0,
    PeerHours = 1,
    Planning = 2,
    Manual = 3
}

public static class PayrollPrimaryTimingSourceLabels
{
    public static string Nl(PayrollPrimaryTimingSource source) => source switch
    {
        PayrollPrimaryTimingSource.OwnGps => "EIGEN GPS",
        PayrollPrimaryTimingSource.PeerHours => "COLLEGA",
        PayrollPrimaryTimingSource.Planning => "PLANNING",
        PayrollPrimaryTimingSource.Manual => "MANUEEL",
        _ => source.ToString()
    };

    public static string Css(PayrollPrimaryTimingSource source) => source switch
    {
        PayrollPrimaryTimingSource.OwnGps => "src-own-gps",
        PayrollPrimaryTimingSource.PeerHours => "src-peer",
        PayrollPrimaryTimingSource.Planning => "src-planning",
        PayrollPrimaryTimingSource.Manual => "src-manual",
        _ => "src-unknown"
    };

    /// <summary>
    /// Derive primary source from how VAN/TOT were chosen — not from EvidenceClass alone.
    /// </summary>
    public static PayrollPrimaryTimingSource Resolve(
        string? intervalSource,
        bool intervalCopiedFromPeer,
        bool intervalFromOwnGps,
        bool intervalFromPlanning)
    {
        if (intervalCopiedFromPeer
            || string.Equals(intervalSource, "peer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intervalSource, "peerSharedTravel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intervalSource, "peerHours", StringComparison.OrdinalIgnoreCase))
        {
            return PayrollPrimaryTimingSource.PeerHours;
        }

        if (intervalFromOwnGps
            || string.Equals(intervalSource, "gpsSite", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intervalSource, "ownGps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intervalSource, "gps", StringComparison.OrdinalIgnoreCase))
        {
            return PayrollPrimaryTimingSource.OwnGps;
        }

        if (intervalFromPlanning
            || string.Equals(intervalSource, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return PayrollPrimaryTimingSource.Planning;
        }

        return PayrollPrimaryTimingSource.Manual;
    }
}

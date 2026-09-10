using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Material create-proposal comparison for stale protection.
/// Ignores workflow-only finding changes (status, decision, comment, timestamps, display text).
/// Compares wall-clock minutes (not raw DateTimeOffset equality / timezone offsets).
/// </summary>
public static class PayrollCreateProposalSemantics
{
    public static bool AreMateriallyEquivalent(
        PayrollActionCreateProposal stored,
        PayrollActionCreateProposal current) =>
        string.Equals(stored.ResourceId, current.ResourceId, StringComparison.Ordinal)
        && stored.Date == current.Date
        && stored.MainTaskId == current.MainTaskId
        && string.Equals(NormalizeProject(stored.ProjectId), NormalizeProject(current.ProjectId), StringComparison.Ordinal)
        && string.Equals(NormalizeBon(stored.BonNr), NormalizeBon(current.BonNr), StringComparison.Ordinal)
        && WallClockMinute(stored.Start) == WallClockMinute(current.Start)
        && WallClockMinute(stored.End) == WallClockMinute(current.End);

    /// <summary>
    /// Canonical fingerprint of the executable create mutation contract (no volatile display/workflow fields).
    /// </summary>
    public static string ComputeFingerprint(PayrollActionCreateProposal proposal)
    {
        var payload = string.Join(
            '|',
            proposal.ResourceId.Trim(),
            proposal.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WallClockMinute(proposal.Start).ToString("HH:mm", CultureInfo.InvariantCulture),
            WallClockMinute(proposal.End).ToString("HH:mm", CultureInfo.InvariantCulture),
            NormalizeProject(proposal.ProjectId),
            NormalizeBon(proposal.BonNr) ?? string.Empty,
            proposal.MainTaskId.ToString(CultureInfo.InvariantCulture),
            ((int)proposal.IntervalSemantics).ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    public static TimeOnly WallClockMinute(DateTimeOffset value) =>
        new(value.Hour, value.Minute);

    public static DateTimeOffset AtWallClock(
        DateOnly date,
        TimeOnly time,
        TimeSpan offset) =>
        new(date.ToDateTime(time), offset);

    public static TimeSpan BelgiumOffsetFor(DateOnly date)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Brussels");
        return zone.GetUtcOffset(date.ToDateTime(new TimeOnly(12, 0)));
    }

    private static string NormalizeProject(string projectId) =>
        projectId.Trim().ToUpperInvariant();

    private static string? NormalizeBon(string? bonNr) =>
        string.IsNullOrWhiteSpace(bonNr) ? null : bonNr.Trim();
}

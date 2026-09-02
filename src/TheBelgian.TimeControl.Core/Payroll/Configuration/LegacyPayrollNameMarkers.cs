namespace TheBelgian.TimeControl.Core.Payroll.Configuration;

/// <summary>
/// Legacy Power Query name markers used only for auto-suggestion filtering.
/// Mirrors Prestaties-overzicht filters Resource_OA / stagair.
/// Not a permanent legal/payroll master invariant — explicit configuration wins.
/// </summary>
public static class LegacyPayrollNameMarkers
{
    /// <summary>
    /// Power Query: Text.Contains([Resource], "OA") — case-sensitive.
    /// </summary>
    public static bool IsLegacyOaMarker(string? displayName) =>
        !string.IsNullOrEmpty(displayName)
        && displayName.Contains("OA", StringComparison.Ordinal);

    /// <summary>
    /// Power Query: Text.Contains([Resource], "stagair") — case-sensitive literal.
    /// Also accepts common "stagiair" spelling for practical source variance.
    /// </summary>
    public static bool IsLegacyStagiairMarker(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return false;
        }

        return displayName.Contains("stagair", StringComparison.OrdinalIgnoreCase);
    }
}

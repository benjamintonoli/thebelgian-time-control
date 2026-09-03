namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class PayrollRosterSelectionRow
{
    public string ResourceId { get; set; } = string.Empty;

    public bool IsOnPayroll { get; set; }
}

public static class PayrollRosterSelectionSplitter
{
    /// <summary>
    /// Only submitted rows are evaluated. Absent (filtered-out) employees are ignored.
    /// </summary>
    public static (IReadOnlyList<string> Included, IReadOnlyList<string> Excluded) Split(
        IReadOnlyList<PayrollRosterSelectionRow>? rows)
    {
        var included = new List<string>();
        var excluded = new List<string>();
        if (rows is null)
        {
            return (included, excluded);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = row.ResourceId?.Trim();
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            if (row.IsOnPayroll)
            {
                included.Add(id);
            }
            else
            {
                excluded.Add(id);
            }
        }

        return (included, excluded);
    }
}

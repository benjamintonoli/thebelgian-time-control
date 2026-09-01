namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

public static class PostcodeNormalizer
{
    public static string? TryNormalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (!trimmed.All(static c => char.IsDigit(c)))
        {
            return null;
        }

        if (trimmed.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(trimmed, out var value) || value is < 1000 or > 9999)
        {
            return null;
        }

        return trimmed;
    }
}

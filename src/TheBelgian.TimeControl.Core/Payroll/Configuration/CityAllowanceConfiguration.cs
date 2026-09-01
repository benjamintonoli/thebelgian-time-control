namespace TheBelgian.TimeControl.Core.Payroll.Configuration;

public sealed record CityAllowanceConfiguration(
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal TripAmount,
    IReadOnlySet<int> QualifyingPostcodes)
{
    public bool IsActiveOn(DateOnly date) =>
        date >= ValidFrom && (ValidTo is null || date <= ValidTo);

    public bool IsQualifyingPostcode(string? normalizedPostcode)
    {
        if (string.IsNullOrWhiteSpace(normalizedPostcode))
        {
            return false;
        }

        return int.TryParse(normalizedPostcode, out var value)
            && QualifyingPostcodes.Contains(value);
    }

    /// <summary>
    /// Explicit July 2026 parity configuration for golden-master tests only.
    /// Not a production seed.
    /// </summary>
    public static CityAllowanceConfiguration July2026Legacy { get; } = new(
        new DateOnly(2026, 7, 1),
        new DateOnly(2026, 7, 31),
        5.00m,
        LegacyCityPostcodes.July2026Qualifying);
}

namespace TheBelgian.TimeControl.Core.Payroll.Configuration;

public sealed record KmAllowanceConfiguration(
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal RatePerKm)
{
    public bool IsActiveOn(DateOnly date) =>
        date >= ValidFrom && (ValidTo is null || date <= ValidTo);

    /// <summary>
    /// Explicit 2026-active configuration for golden-master / current shadow tests only.
    /// Not a production seed.
    /// </summary>
    public static KmAllowanceConfiguration Year2026Legacy { get; } = new(
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 12, 31),
        0.1448m);
}

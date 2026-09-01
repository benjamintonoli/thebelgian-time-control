namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record PostcodeResolutionResult(
    string? Postcode,
    PostcodeResolutionSource Source)
{
    public static PostcodeResolutionResult Unresolved { get; } =
        new(null, PostcodeResolutionSource.Unresolved);

    public bool IsResolved => Postcode is not null;
}

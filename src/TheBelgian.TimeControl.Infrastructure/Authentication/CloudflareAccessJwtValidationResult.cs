namespace TheBelgian.TimeControl.Infrastructure.Authentication;

public sealed record CloudflareAccessJwtValidationResult(
    bool IsValid,
    string? Email,
    string? Subject,
    string? DisplayName,
    string? Error)
{
    public static CloudflareAccessJwtValidationResult Success(
        string email,
        string subject,
        string? displayName) =>
        new(true, email, subject, displayName, null);

    public static CloudflareAccessJwtValidationResult Failure(string error) =>
        new(false, null, null, null, error);
}

public interface ICloudflareAccessJwtValidator
{
    Task<CloudflareAccessJwtValidationResult> ValidateAsync(
        string jwt,
        CancellationToken cancellationToken);
}

namespace TheBelgian.TimeControl.Core.Models;

public sealed record AuthenticatedActor(
    string Email,
    string Subject,
    string? DisplayName)
{
    public string AuditIdentity => Email;

    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? Email
        : DisplayName;
}

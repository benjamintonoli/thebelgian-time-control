using Microsoft.IdentityModel.Tokens;

namespace TheBelgian.TimeControl.Infrastructure.Authentication;

public interface ICloudflareAccessCertificateProvider
{
    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SecurityKey>> RefreshSigningKeysAsync(CancellationToken cancellationToken);
}

using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Persists the private ECDSA trust key owned by one application gateway identity.
/// </summary>
/// <remarks>
/// The default implementation uses the gateway's application-scoped local state directory.
/// Platform gateways may replace it with a native durable secret repository. Returned keys
/// transfer ownership to the caller and must use the NIST P-256 curve.
/// </remarks>
public interface IGatewayTrustKeyRepository
{
    /// <summary>Loads the durable trust key or creates it atomically when absent.</summary>
    /// <param name="application">The application that issues credentials.</param>
    /// <param name="gateway">The gateway identity within that application.</param>
    /// <param name="cancellationToken">Cancels repository access.</param>
    /// <returns>An owned ECDSA P-256 private key.</returns>
    Task<ECDsa> LoadOrCreateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the durable trust key with a newly generated key.</summary>
    /// <param name="application">The application that issues credentials.</param>
    /// <param name="gateway">The gateway identity within that application.</param>
    /// <param name="cancellationToken">Cancels repository access.</param>
    /// <returns>The newly persisted, owned ECDSA P-256 private key.</returns>
    Task<ECDsa> RotateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken = default);
}

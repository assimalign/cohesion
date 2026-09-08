using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Manages the developer and peer-trust operations owned by an application gateway.
/// </summary>
public interface IApplicationTrustGateway : ITrustedIssuerProvider
{
    /// <summary>Issues a short-lived developer export token.</summary>
    /// <param name="model">The application issuing the token.</param>
    /// <param name="developerName">The developer principal name.</param>
    /// <param name="cancellationToken">Cancels issuance.</param>
    /// <returns>An ES256 compact JSON Web Token with audience <c>cohesion-export</c>.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="developerName"/> is empty.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task<string> IssueDeveloperTokenAsync(
        IApplicationModel model,
        string developerName,
        CancellationToken cancellationToken = default);

    /// <summary>Adds the public trust key from a peer application export.</summary>
    /// <param name="model">The application receiving the grant.</param>
    /// <param name="peerName">The expected peer application name.</param>
    /// <param name="export">The peer's validated export.</param>
    /// <param name="cancellationToken">Cancels the grant.</param>
    /// <returns>A task that completes after the grant is durable.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="model"/> or <paramref name="export"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.ArgumentException"><paramref name="peerName"/> is empty.</exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// The export is invalid, identifies a different peer, identifies the receiving application,
    /// or contains an invalid public trust key.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The application's own SecretStore cannot durably accept the grant.
    /// </exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task AddTrustedIssuerAsync(
        IApplicationModel model,
        string peerName,
        ApplicationExportDocument export,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates the application's issuing trust key. Tokens signed by the previous key stop
    /// validating after each verifier replaces that application's persisted trust grant.
    /// </summary>
    /// <param name="model">The application whose issuing key is rotated.</param>
    /// <param name="cancellationToken">Cancels rotation.</param>
    /// <returns>A task that completes after the replacement key is durable.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task RotateTrustKeyAsync(
        IApplicationModel model,
        CancellationToken cancellationToken = default);
}

using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Supplies the credential used to dispatch a command to one realized resource.
/// </summary>
public interface IResourceCommandCredentialProvider
{
    /// <summary>
    /// Gets the resource-scoped bearer credential for command dispatch: the application's
    /// <see cref="ApplicationCredentialPurpose.ResourceAccess"/> credential for the current reconcile
    /// pass, with <paramref name="resource"/> as its audience.
    /// </summary>
    /// <param name="application">The application that owns the realized resource.</param>
    /// <param name="resource">The target resource.</param>
    /// <param name="cancellationToken">Signals that issuance should be abandoned.</param>
    /// <returns>
    /// The bearer credential minted by the application's registered
    /// <see cref="IApplicationCredentialIssuer"/>, or by the gateway's default ES256 application-key
    /// issuer when none is registered or it defers.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// The application is not active in the gateway session, or its registered credential issuer
    /// returned a credential that cannot be presented as a bearer token.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    ValueTask<string> GetResourceCommandCredentialAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default);
}

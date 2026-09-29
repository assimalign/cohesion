using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Authenticates callers of a gateway control plane that the built-in ES256 trusted-issuer
/// authenticator does not recognize. Registered in <see cref="ApplicationProviders.Callers"/>
/// and consulted in registration order after the built-in authenticator.
/// </summary>
/// <remarks>
/// <para>
/// The control plane parses <c>Authorization: &lt;scheme&gt; &lt;credential&gt;</c>. The built-in
/// authenticator verifies a <c>Bearer</c> application-key token from a trusted issuer; any credential
/// it does not verify — another scheme, another issuer, or an invalid token — reaches the registered
/// authenticators in order. The first <see cref="ApplicationCallerStatus.Authenticated"/> result
/// admits the call; <see cref="ApplicationCallerStatus.Unauthorized"/> answers <c>401</c> and
/// <see cref="ApplicationCallerStatus.Forbidden"/> answers <c>403</c>, each with
/// <see cref="ApplicationCallerResult.Failure"/> as the error body when present. When every
/// authenticator returns <see cref="ApplicationCallerStatus.NoResult"/> the call is refused with
/// <c>403</c>, as it is when nothing is registered.
/// </para>
/// <para>
/// The control plane authorizes the mapped <see cref="ApplicationCaller"/>, never the raw credential:
/// discovery routes admit any authenticated caller; command routes require a
/// <see cref="ApplicationCallerKind.Peer"/> caller with an <see cref="ApplicationCaller.Application"/>,
/// which owns every command it sends or reads, limited to
/// <see cref="ApplicationCaller.AllowedCommandKinds"/> (empty permits every kind).
/// </para>
/// </remarks>
public interface IApplicationCallerAuthenticator
{
    /// <summary>
    /// Authenticates one presented credential.
    /// </summary>
    /// <param name="request">The presented credential.</param>
    /// <param name="trustedIssuers">The application's current trusted issuers.</param>
    /// <param name="cancellationToken">Signals that authentication should be abandoned.</param>
    /// <returns>
    /// <see cref="ApplicationCallerStatus.NoResult"/> to let the next authenticator decide, or a
    /// final <see cref="ApplicationCallerStatus.Authenticated"/>,
    /// <see cref="ApplicationCallerStatus.Unauthorized"/>, or
    /// <see cref="ApplicationCallerStatus.Forbidden"/> result.
    /// </returns>
    ValueTask<ApplicationCallerResult> AuthenticateAsync(
        ApplicationCallerRequest request,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        CancellationToken cancellationToken = default);
}

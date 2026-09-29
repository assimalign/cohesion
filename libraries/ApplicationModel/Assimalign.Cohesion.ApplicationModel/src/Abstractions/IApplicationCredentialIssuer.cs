using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Mints the credentials a gateway hands to resources, peers, and developers. Registered through
/// <see cref="ApplicationProviders.CredentialIssuer"/> and consulted before the gateway's default
/// ES256 application-key issuer for every credential the gateway mints.
/// </summary>
/// <remarks>
/// <para>
/// An issuer may handle only some <see cref="ApplicationCredentialPurpose"/> values and defer the
/// rest by returning <see langword="null"/>. The gateway fills each request as follows (the default
/// issuer signs exactly these values, adding the <c>scope=telemetry</c> claim for
/// <see cref="ApplicationCredentialPurpose.Telemetry"/> and <c>cohesion_token_use=gateway</c> for
/// <see cref="ApplicationCredentialPurpose.RemoteCommand"/> and
/// <see cref="ApplicationCredentialPurpose.PeerControlPlane"/>):
/// </para>
/// <list type="table">
/// <listheader><term>Purpose</term><description>Audience, subject, lifetime</description></listheader>
/// <item><term><see cref="ApplicationCredentialPurpose.ResourceBootstrap"/></term><description>the resource, the gateway, the bootstrap lifetime</description></item>
/// <item><term><see cref="ApplicationCredentialPurpose.ResourceAccess"/></term><description>the resource, the gateway, the bootstrap lifetime</description></item>
/// <item><term><see cref="ApplicationCredentialPurpose.Telemetry"/></term><description>the sink resource, the emitting resource, the bootstrap lifetime</description></item>
/// <item><term><see cref="ApplicationCredentialPurpose.RemoteCommand"/></term><description><c>cohesion-export</c>, the gateway, the developer-token lifetime</description></item>
/// <item><term><see cref="ApplicationCredentialPurpose.PeerControlPlane"/></term><description><c>cohesion-export</c>, the gateway, the developer-token lifetime</description></item>
/// <item><term><see cref="ApplicationCredentialPurpose.Developer"/></term><description><c>cohesion-export</c>, the developer, the developer-token lifetime</description></item>
/// </list>
/// <para>
/// The gateway caches resource and telemetry credentials for one reconcile pass, and asks again once a
/// cached credential's <see cref="ApplicationCredential.ExpiresAt"/> has passed. Every carrier but
/// one presents the credential as <c>Authorization: Bearer</c> (the bootstrap file, the resource
/// probes, command delivery, provider connections, the peer and developer clients), so the gateway
/// refuses any other <see cref="ApplicationCredential.Scheme"/> for those purposes with an
/// <see cref="System.InvalidOperationException"/>. The telemetry headers document writes the
/// credential's own scheme. An exception from the issuer is not caught: it fails the operation that
/// needed the credential (a command delivery records it as a rejection).
/// </para>
/// <para>
/// Resources verify what the issuer mints, so an application that registers one must register a
/// matching credential verifier on its resources: <c>ResourceRuntime.RegisterCredentialVerifier</c> in
/// <c>Assimalign.Cohesion.Hosting.Resources</c>, as the runtime contract (<c>docs/RUNTIME_CONTRACT.md</c>,
/// <em>Resource credentials</em>) requires. A peer gateway that receives the application's
/// <see cref="ApplicationCredentialPurpose.RemoteCommand"/> or
/// <see cref="ApplicationCredentialPurpose.PeerControlPlane"/> credential authenticates it through an
/// <see cref="IApplicationCallerAuthenticator"/>.
/// </para>
/// </remarks>
public interface IApplicationCredentialIssuer
{
    /// <summary>
    /// Mints one credential.
    /// </summary>
    /// <param name="request">The credential to mint.</param>
    /// <param name="cancellationToken">Signals that issuance should be abandoned.</param>
    /// <returns>
    /// The credential, or <see langword="null"/> to defer this request to the gateway's default
    /// ES256 application-key issuer.
    /// </returns>
    ValueTask<ApplicationCredential?> IssueAsync(
        ApplicationCredentialRequest request,
        CancellationToken cancellationToken = default);
}

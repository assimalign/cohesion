namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies why a gateway mints a credential, so an <see cref="IApplicationCredentialIssuer"/>
/// can issue some purposes itself and defer the rest to the gateway's default issuer.
/// </summary>
public enum ApplicationCredentialPurpose
{
    /// <summary>
    /// The resource's own bootstrap credential, delivered through the file named by
    /// <see cref="AppEnvironment.Variables.BootstrapTokenPath"/> or the ambient resource context.
    /// The gateway's readiness and health probes present this same credential to the resource's
    /// control plane, so a registered verifier must admit it as the gateway
    /// (<c>ResourceCallerKind.Gateway</c>, subject = the gateway).
    /// </summary>
    ResourceBootstrap = 0,

    /// <summary>
    /// The gateway calling a resource control plane to deliver commands, including store reads made
    /// on behalf of a mount source, certificate authority, or trust store. Readiness and health
    /// probes are the exception: they present the <see cref="ResourceBootstrap"/> credential.
    /// </summary>
    ResourceAccess,

    /// <summary>
    /// A resource exporting telemetry to the application's telemetry sink.
    /// </summary>
    Telemetry,

    /// <summary>
    /// The gateway delivering a command to a resource owned by another application's gateway.
    /// </summary>
    RemoteCommand,

    /// <summary>
    /// The gateway calling a peer gateway's control plane.
    /// </summary>
    PeerControlPlane,

    /// <summary>
    /// A developer credential exported for tooling that calls the gateway control plane.
    /// </summary>
    Developer,
}

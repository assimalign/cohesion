using System;
using System.Net.Security;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The authenticated connection a gateway hands a provider so it can call the control plane of
/// the model resource that backs it (for example a secret store or certificate authority).
/// </summary>
/// <param name="Caller">The application on whose behalf the gateway calls the resource.</param>
/// <param name="Resource">The backing resource.</param>
/// <param name="ResourceKind">The backing resource's manifest kind.</param>
/// <param name="ControlPlaneAddress">
/// The resource's control-plane base address: its observed control-plane endpoint combined with
/// the manifest's control-plane path.
/// </param>
/// <param name="BearerCredential">
/// The credential to present as <c>Authorization: Bearer</c>: the application's
/// <see cref="ApplicationCredentialPurpose.ResourceAccess"/> credential for the reconcile pass,
/// with the backing resource as audience. The gateway mints it through
/// <see cref="ApplicationProviders.CredentialIssuer"/>, or with the application's ES256 trust key
/// when no issuer is registered or it defers.
/// </param>
/// <param name="ServerCertificateValidator">
/// The validator for the resource's TLS certificate, anchored in the application's transport
/// trust, or <see langword="null"/> when the platform default validation applies.
/// </param>
/// <remarks>
/// <see cref="object.ToString"/> redacts <see cref="BearerCredential"/>; never log the credential
/// itself.
/// </remarks>
public sealed record ResourceProviderConnection(
    ApplicationName Caller,
    ResourceName Resource,
    string ResourceKind,
    Uri ControlPlaneAddress,
    string BearerCredential,
    RemoteCertificateValidationCallback? ServerCertificateValidator)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Caller = ").Append(Caller.ToString())
            .Append(", Resource = ").Append(Resource.ToString())
            .Append(", ResourceKind = ").Append(ResourceKind)
            .Append(", ControlPlaneAddress = ").Append(ControlPlaneAddress)
            .Append(", BearerCredential = <redacted>");
        return true;
    }
}

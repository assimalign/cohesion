using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Verifies credentials presented to a gateway-managed resource and maps them to a
/// <see cref="ResourceCaller"/>.
/// </summary>
/// <remarks>
/// <para>
/// The default resource credential is the ES256 application-key JSON Web Token described by the runtime
/// contract; every area hosting module verifies it without registration. An application whose gateway
/// registers a different credential issuer registers a matching verifier on each resource with
/// <see cref="ResourceRuntime.RegisterCredentialVerifier(System.Reflection.Assembly, System.Func{ResourceContext, IResourceCredentialVerifier})"/>.
/// </para>
/// <para>
/// The hosting module consults the registered verifier first. <see cref="ResourceCredentialStatus.NoResult"/>
/// falls through to the default application-key verification; any other status is final. The module
/// then authorizes the mapped caller exactly as it authorizes an application-key caller, comparing
/// <see cref="ResourceCaller.Application"/>, <see cref="ResourceCaller.Subject"/>, and
/// <see cref="ResourceCaller.Kind"/> rather than raw token claims.
/// </para>
/// </remarks>
public interface IResourceCredentialVerifier
{
    /// <summary>Verifies one presented credential.</summary>
    /// <param name="presentation">The presented credential and the audience it must be issued for.</param>
    /// <param name="cancellationToken">A token that cancels verification.</param>
    /// <returns>
    /// The verification outcome; <see cref="ResourceCredentialStatus.NoResult"/> when the verifier does not
    /// recognize the credential.
    /// </returns>
    ValueTask<ResourceCredentialVerification> VerifyAsync(
        ResourceCredentialPresentation presentation,
        CancellationToken cancellationToken = default);
}

using System;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Carries one credential a caller presented to a resource, with the audience and instant it must be
/// verified against.
/// </summary>
/// <param name="Scheme">
/// The presentation scheme, such as <c>Bearer</c> for an HTTP <c>Authorization</c> header, or an empty
/// string when the caller presented no scheme.
/// </param>
/// <param name="Credential">The credential text following the scheme, or an empty string when absent.</param>
/// <param name="ExpectedAudience">The resource name the credential must be issued for.</param>
/// <param name="Now">The instant temporal rules are evaluated at.</param>
/// <param name="ClientCertificate">
/// The transport client certificate, when the listener requested and received one; otherwise,
/// <see langword="null"/>. The verifier must not dispose it.
/// </param>
/// <remarks>
/// <see cref="object.ToString"/> redacts <see cref="Credential"/>; never log the credential itself.
/// </remarks>
public sealed record ResourceCredentialPresentation(
    string Scheme,
    string Credential,
    string ExpectedAudience,
    DateTimeOffset Now,
    X509Certificate2? ClientCertificate = null)
{
    /// <summary>
    /// Creates a presentation from an authorization value of the form <c>&lt;scheme&gt; &lt;credential&gt;</c>,
    /// such as an HTTP <c>Authorization</c> header.
    /// </summary>
    /// <param name="authorization">The authorization value, or <see langword="null"/> when none was presented.</param>
    /// <param name="expectedAudience">The resource name the credential must be issued for.</param>
    /// <param name="now">The instant temporal rules are evaluated at.</param>
    /// <param name="clientCertificate">The transport client certificate, if any.</param>
    /// <returns>
    /// A presentation whose <see cref="Scheme"/> is the text before the first space and whose
    /// <see cref="Credential"/> is everything after it, unmodified; both are empty when
    /// <paramref name="authorization"/> is <see langword="null"/>, and <see cref="Credential"/> is empty
    /// when the value has no space.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="expectedAudience"/> is <see langword="null"/>.</exception>
    public static ResourceCredentialPresentation FromAuthorizationValue(
        string? authorization,
        string expectedAudience,
        DateTimeOffset now,
        X509Certificate2? clientCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(expectedAudience);

        string value = authorization ?? string.Empty;
        int separator = value.IndexOf(' ');
        return new ResourceCredentialPresentation(
            separator < 0 ? value : value[..separator],
            separator < 0 ? string.Empty : value[(separator + 1)..],
            expectedAudience,
            now,
            clientCertificate);
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Scheme = ").Append(Scheme)
            .Append(", Credential = <redacted>, ExpectedAudience = ").Append(ExpectedAudience)
            .Append(", Now = ").Append(Now.ToString("O", null))
            .Append(", ClientCertificate = ").Append(ClientCertificate?.Thumbprint);
        return true;
    }
}

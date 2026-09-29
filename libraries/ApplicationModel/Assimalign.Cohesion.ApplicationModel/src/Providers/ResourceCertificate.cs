using System;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A TLS certificate returned by an <see cref="IResourceCertificateAuthority"/> or an
/// <see cref="IResourceSourceProvider"/>, together with the anchors that validate it.
/// </summary>
/// <param name="CertificatePem">
/// The PEM bundle written to the endpoint's certificate mount: the leaf certificate, any
/// intermediates, and the leaf's PKCS#8 private key.
/// </param>
/// <param name="TrustAnchorsPem">
/// The PEM-encoded root certificates that validate the leaf. The gateway adds them to the
/// application's transport trust bundle.
/// </param>
/// <exception cref="ArgumentNullException">
/// <paramref name="CertificatePem"/> or <paramref name="TrustAnchorsPem"/> is <see langword="null"/>.
/// </exception>
/// <remarks>
/// <see cref="object.ToString"/> redacts <see cref="CertificatePem"/> because it carries a private key.
/// </remarks>
public sealed record ResourceCertificate(string CertificatePem, string TrustAnchorsPem)
{
    /// <summary>
    /// Gets the PEM bundle written to the endpoint's certificate mount.
    /// </summary>
    public string CertificatePem { get; init; } =
        CertificatePem ?? throw new ArgumentNullException(nameof(CertificatePem));

    /// <summary>
    /// Gets the PEM-encoded root certificates that validate the leaf.
    /// </summary>
    public string TrustAnchorsPem { get; init; } =
        TrustAnchorsPem ?? throw new ArgumentNullException(nameof(TrustAnchorsPem));

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("CertificatePem = <redacted>, TrustAnchorsPem = ").Append(TrustAnchorsPem);
        return true;
    }
}

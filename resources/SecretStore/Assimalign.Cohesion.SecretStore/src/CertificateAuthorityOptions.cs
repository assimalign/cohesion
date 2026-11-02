using System;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Configures the certificate authority owned by a secret store application.
/// </summary>
/// <remarks>
/// <para>
/// This type carries composition data only. The area root performs no certificate generation,
/// persistence, or network access.
/// </para>
/// <para>
/// Existing durable authority state takes precedence over every property. On first start,
/// <see cref="InitialCertificate"/> and <see cref="InitialPrivateKey"/> take precedence over
/// self-seeding, which <see cref="SelfSeed"/> controls.
/// </para>
/// </remarks>
public sealed class CertificateAuthorityOptions
{
    /// <summary>
    /// Gets or sets the common name used when creating a self-signed root certificate.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>Cohesion SecretStore Certificate Authority</c> so composition is
    /// deterministic. The value must not be empty or whitespace.
    /// </remarks>
    public string CommonName { get; set; } = "Cohesion SecretStore Certificate Authority";

    /// <summary>
    /// Gets or sets whether a first-start store with no initial authority material creates a
    /// self-signed development root certificate.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. When it is <see langword="false"/>, the store requires
    /// <see cref="InitialCertificate"/> and <see cref="InitialPrivateKey"/>.
    /// </remarks>
    public bool SelfSeed { get; set; } = true;

    /// <summary>
    /// Gets or sets a PEM-encoded certificate with which to seed the authority on first start.
    /// </summary>
    /// <remarks>
    /// This property and <see cref="InitialPrivateKey"/> must either both be supplied or both be
    /// <see langword="null"/>. The builder snapshots supplied bytes. Durable state, when present,
    /// takes precedence over this material.
    /// </remarks>
    public ReadOnlyMemory<byte>? InitialCertificate { get; set; }

    /// <summary>
    /// Gets or sets the PEM-encoded PKCS#8 private key paired with
    /// <see cref="InitialCertificate"/>.
    /// </summary>
    /// <remarks>
    /// This property and <see cref="InitialCertificate"/> must either both be supplied or both be
    /// <see langword="null"/>. The builder snapshots supplied bytes. Callers should clear their
    /// source buffer when it is no longer needed.
    /// </remarks>
    public ReadOnlyMemory<byte>? InitialPrivateKey { get; set; }
}

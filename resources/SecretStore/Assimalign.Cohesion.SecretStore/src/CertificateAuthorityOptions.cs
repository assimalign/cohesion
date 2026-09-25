using System;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Configures the certificate authority owned by a secret store application.
/// </summary>
/// <remarks>
/// <para>
/// This type carries composition data only. The area root performs no enrollment, certificate
/// generation, persistence, or network access.
/// </para>
/// <para>
/// Existing durable authority state takes precedence over every property. On first start,
/// <see cref="InitialCertificate"/> and <see cref="InitialPrivateKey"/> take precedence over
/// <see cref="PlatformEnrollmentEndpoint"/>. Self-seeding is considered only when neither source
/// is configured.
/// </para>
/// </remarks>
public sealed class CertificateAuthorityOptions
{
    /// <summary>
    /// Gets or sets the common name used when creating a standalone root certificate.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>Cohesion SecretStore Certificate Authority</c> so standalone composition is
    /// deterministic. Platform enrollment uses the ambient application/resource identity as the
    /// intermediate subject. The value must not be empty or whitespace.
    /// </remarks>
    public string CommonName { get; set; } = "Cohesion SecretStore Certificate Authority";

    /// <summary>
    /// Gets or sets whether a first-start store with no initial authority material and no Platform
    /// enrollment endpoint creates a self-signed development root certificate.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. This setting does not permit fallback when a configured
    /// Platform enrollment attempt fails, because doing so would create a separate trust hierarchy.
    /// </remarks>
    public bool SelfSeedWhenNoPlatform { get; set; } = true;

    /// <summary>
    /// Gets or sets the absolute HTTPS endpoint used to enroll this store as an intermediate
    /// certificate authority with the Platform secret store.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> value disables Platform enrollment. The hosting implementation
    /// creates durable pending enrollment state for the gateway-mediated request, signing, and
    /// completion protocol; this root contract does not perform network access.
    /// </remarks>
    public Uri? PlatformEnrollmentEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the PEM-encoded Platform certificate used as the trust anchor and certificate
    /// pin for HTTPS enrollment.
    /// </summary>
    /// <remarks>
    /// The value is optional. When supplied, it must be non-empty and is snapshotted by the builder.
    /// </remarks>
    public ReadOnlyMemory<byte>? PlatformCertificate { get; set; }

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

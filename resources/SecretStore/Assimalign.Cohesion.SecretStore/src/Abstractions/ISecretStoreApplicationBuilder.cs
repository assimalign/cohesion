using System;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Defines the contract-only composition seam for a secret store application.
/// </summary>
public interface ISecretStoreApplicationBuilder
{
    /// <summary>
    /// Declares an initial secret at a logical store path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value seeds the path only when durable state does not already contain a version at
    /// that path. A restart therefore never rolls back a value that was rotated after seeding.
    /// </para>
    /// <para>
    /// Implementations snapshot <paramref name="value"/> during registration. Paths are compared
    /// using ordinal semantics and are not normalized.
    /// </para>
    /// </remarks>
    /// <param name="path">The logical path at which to seed the secret.</param>
    /// <param name="value">The secret bytes to seed.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty or whitespace, or names an implementation-reserved path.
    /// </exception>
    /// <exception cref="InvalidOperationException">The path has already been declared on this builder.</exception>
    ISecretStoreApplicationBuilder AddSecret(string path, ReadOnlyMemory<byte> value);

    /// <summary>
    /// Declares the certificate authority owned by this secret store.
    /// </summary>
    /// <remarks>
    /// Durable certificate-authority state always wins over composition-time seed material. On a
    /// first start, paired initial PEM material is used when supplied; otherwise a configured
    /// Platform endpoint selects gateway-mediated intermediate enrollment. When neither is configured,
    /// <see cref="CertificateAuthorityOptions.SelfSeedWhenNoPlatform"/> controls whether the store
    /// creates a self-signed development root. A failed configured Platform enrollment never
    /// silently falls back to an unrelated self-signed root.
    /// </remarks>
    /// <param name="configure">An optional callback that configures the certificate authority.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The common name is empty; the Platform enrollment endpoint is not absolute HTTPS; configured
    /// PEM material is empty; or an initial certificate and private key are not supplied together.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A certificate authority has already been declared, or no first-start authority source is
    /// enabled.
    /// </exception>
    ISecretStoreApplicationBuilder AddCertificateAuthority(
        Action<CertificateAuthorityOptions>? configure = null);

    /// <summary>
    /// Builds the secret store application.
    /// </summary>
    /// <returns>The configured secret store application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, a registered service factory returns
    /// <see langword="null"/>, or the ambient endpoint or data mount cannot host the store.
    /// </exception>
    ISecretStoreApplication Build();
}

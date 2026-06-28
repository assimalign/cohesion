using System;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Security;

/// <summary>
/// Loads X.509 certificates from common, platform-agnostic sources (PKCS#12 / PFX
/// and PEM).
/// </summary>
/// <remarks>
/// <para>
/// This is the minimal, cross-platform certificate-loading surface; every member
/// resolves to the platform-neutral BCL loaders, so behavior is identical on
/// Windows, Linux, and macOS.
/// </para>
/// <para>
/// Operating-system certificate <em>store</em> access — the Windows certificate
/// store, the macOS keychain, and Linux trust directories — is intentionally out
/// of scope for this contract and is tracked separately.
/// </para>
/// </remarks>
public interface ICertificateManager
{
    /// <summary>
    /// Loads a certificate (and its private key, when present) from a PKCS#12 / PFX file.
    /// </summary>
    /// <param name="path">The path to the <c>.pfx</c> / <c>.p12</c> file.</param>
    /// <param name="password">The PKCS#12 password, or <see langword="null"/> when the file is unprotected.</param>
    /// <returns>The loaded certificate.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="CertificateException">The file is missing or cannot be loaded as a PKCS#12 certificate.</exception>
    X509Certificate2 LoadPkcs12FromFile(string path, string? password = null);

    /// <summary>
    /// Loads a certificate (and its private key, when present) from PKCS#12 / PFX bytes.
    /// </summary>
    /// <param name="data">The PKCS#12 / PFX content.</param>
    /// <param name="password">The PKCS#12 password, or <see langword="null"/> when the content is unprotected.</param>
    /// <returns>The loaded certificate.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    /// <exception cref="CertificateException">The content cannot be loaded as a PKCS#12 certificate.</exception>
    X509Certificate2 LoadPkcs12(ReadOnlyMemory<byte> data, string? password = null);

    /// <summary>
    /// Loads a certificate from a PEM file, optionally pairing it with a private-key PEM file.
    /// </summary>
    /// <param name="certificatePath">The path to the PEM certificate file.</param>
    /// <param name="privateKeyPath">
    /// The path to the PEM private-key file to pair with the certificate, or
    /// <see langword="null"/> to load the certificate without a private key.
    /// </param>
    /// <returns>The loaded certificate.</returns>
    /// <exception cref="ArgumentException"><paramref name="certificatePath"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="CertificateException">A file is missing or cannot be loaded as a PEM certificate.</exception>
    X509Certificate2 LoadPemFromFile(string certificatePath, string? privateKeyPath = null);
}

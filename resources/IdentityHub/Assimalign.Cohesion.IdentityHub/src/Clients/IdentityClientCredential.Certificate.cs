using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A certificate of an app registration: the public half of a key pair whose private key the
/// application holds and proves possession of.
/// </summary>
/// <remarks>
/// <para>
/// The credential keeps the public certificate only. Nothing in it is secret, so it is stored
/// as it is, and the application's private key never reaches the hub.
/// </para>
/// <para>
/// The certificate's own validity period becomes the credential's
/// <see cref="IdentityClientCredential.NotBefore"/> and
/// <see cref="IdentityClientCredential.ExpiresAt"/>. The hub does not build a chain or check
/// revocation: registering a certificate is the statement that this key is trusted for this
/// application, as it is for a self-signed one.
/// </para>
/// </remarks>
public sealed class IdentityClientCertificateCredential : IdentityClientCredential
{
    /// <summary>
    /// The largest certificate, in bytes of its DER encoding.
    /// </summary>
    public const int MaxCertificateLength = 8 * 1024;

    // A PEM file is the same certificate in base64 with a header and a footer.
    private const int maxInputLength = MaxCertificateLength * 2;

    private IdentityClientCertificateCredential(
        IdentityClientCredentialId id,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt,
        byte[] certificate,
        string thumbprint)
        : base(id, displayName, notBefore, expiresAt)
    {
        Certificate = certificate;
        Thumbprint = thumbprint;
    }

    /// <inheritdoc />
    public override IdentityCredentialKind Kind => IdentityCredentialKind.Certificate;

    /// <summary>
    /// Gets the public certificate, in its DER encoding.
    /// </summary>
    public ReadOnlyMemory<byte> Certificate { get; }

    /// <summary>
    /// Gets the SHA-256 thumbprint of the certificate, as 64 uppercase hexadecimal characters.
    /// </summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Creates a credential from a public certificate.
    /// </summary>
    /// <param name="id">The credential's identifier.</param>
    /// <param name="certificate">
    /// One X.509 certificate, DER or PEM encoded, without a private key. It is copied.
    /// </param>
    /// <param name="displayName">The name a person is shown for the credential, or <see langword="null"/> for none.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is <see langword="default"/>; <paramref name="certificate"/> is empty,
    /// is not one X.509 certificate, or is larger than <see cref="MaxCertificateLength"/>; or
    /// <paramref name="displayName"/> breaks its rule.
    /// </exception>
    public static IdentityClientCertificateCredential Create(
        IdentityClientCredentialId id,
        ReadOnlyMemory<byte> certificate,
        string? displayName = null)
    {
        if (certificate.IsEmpty || certificate.Length > maxInputLength)
        {
            throw new ArgumentException(ErrorMessages.CredentialCertificateInvalid, nameof(certificate));
        }

        byte[] encoded;
        DateTimeOffset notBefore;
        DateTimeOffset expiresAt;
        try
        {
            using X509Certificate2 parsed = X509CertificateLoader.LoadCertificate(certificate.Span);
            encoded = parsed.RawData;
            notBefore = new DateTimeOffset(parsed.NotBefore.ToUniversalTime());
            expiresAt = new DateTimeOffset(parsed.NotAfter.ToUniversalTime());
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(ErrorMessages.CredentialCertificateInvalid, nameof(certificate), exception);
        }

        if (encoded.Length > MaxCertificateLength)
        {
            throw new ArgumentException(ErrorMessages.CredentialCertificateInvalid, nameof(certificate));
        }

        return new IdentityClientCertificateCredential(
            id,
            displayName,
            notBefore,
            expiresAt,
            encoded,
            Convert.ToHexString(SHA256.HashData(encoded)));
    }

    /// <summary>
    /// Rebuilds a credential from the certificate a codec stored. The certificate is not parsed
    /// again: its validity period was read when the credential was created, and the thumbprint
    /// is derived from the stored bytes.
    /// </summary>
    /// <exception cref="FormatException">The stored certificate is not one this type writes.</exception>
    internal static IdentityClientCertificateCredential Restore(
        IdentityClientCredentialId id,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt,
        byte[]? certificate)
    {
        if (certificate is null || certificate.Length is 0 or > MaxCertificateLength ||
            notBefore is null || expiresAt is null)
        {
            throw new FormatException(ErrorMessages.CredentialCertificateInvalid);
        }

        byte[] encoded = (byte[])certificate.Clone();
        return new IdentityClientCertificateCredential(
            id,
            displayName,
            notBefore,
            expiresAt,
            encoded,
            Convert.ToHexString(SHA256.HashData(encoded)));
    }
}

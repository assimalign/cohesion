using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Security;

/// <summary>
/// Default <see cref="ICertificateManager"/> backed by the platform-neutral BCL
/// certificate loaders (<see cref="X509CertificateLoader"/> and
/// <see cref="X509Certificate2.CreateFromPemFile(string, string?)"/>).
/// </summary>
internal sealed class CertificateManager : ICertificateManager
{
    /// <inheritdoc />
    public X509Certificate2 LoadPkcs12FromFile(string path, string? password = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("The certificate path must be provided.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new CertificateException($"The certificate file '{path}' was not found.");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (CryptographicException exception)
        {
            throw new CertificateException($"The file '{path}' could not be loaded as a PKCS#12 certificate.", exception);
        }
    }

    /// <inheritdoc />
    public X509Certificate2 LoadPkcs12(ReadOnlyMemory<byte> data, string? password = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("The certificate data must not be empty.", nameof(data));
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(data.Span, password);
        }
        catch (CryptographicException exception)
        {
            throw new CertificateException("The provided data could not be loaded as a PKCS#12 certificate.", exception);
        }
    }

    /// <inheritdoc />
    public X509Certificate2 LoadPemFromFile(string certificatePath, string? privateKeyPath = null)
    {
        if (string.IsNullOrEmpty(certificatePath))
        {
            throw new ArgumentException("The certificate path must be provided.", nameof(certificatePath));
        }

        if (!File.Exists(certificatePath))
        {
            throw new CertificateException($"The certificate file '{certificatePath}' was not found.");
        }

        if (privateKeyPath is not null && !File.Exists(privateKeyPath))
        {
            throw new CertificateException($"The private-key file '{privateKeyPath}' was not found.");
        }

        try
        {
            // CreateFromPemFile pairs the certificate with a private key and requires one;
            // when no key is requested, load the certificate alone from its PEM content.
            return privateKeyPath is null
                ? X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath))
                : X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
        }
        catch (CryptographicException exception)
        {
            throw new CertificateException($"The file '{certificatePath}' could not be loaded as a PEM certificate.", exception);
        }
    }
}

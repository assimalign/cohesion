using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

/// <summary>
/// Creates throwaway self-signed certificates for loopback QUIC handshakes.
/// </summary>
internal static class QuicTestCertificate
{
    /// <summary>
    /// Creates a self-signed server certificate for <c>localhost</c> with the SAN, key-usage,
    /// and server-authentication EKU entries the platform QUIC TLS stack expects.
    /// </summary>
    public static X509Certificate2 Create()
    {
        using RSA rsa = RSA.Create(2048);

        CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder sanBuilder = new();

        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);

        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // id-kp-serverAuth
            critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(10));

        // Round-trip through PFX so the private key is persisted in a form the platform TLS
        // stack can use; certificates with ephemeral keys are rejected on Windows.
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable);
    }
}

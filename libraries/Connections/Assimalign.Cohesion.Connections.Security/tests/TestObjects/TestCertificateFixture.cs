using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// An xUnit class fixture that creates the ephemeral self-signed RSA certificates shared by every test
/// in the class: a server certificate (CN/SAN <c>localhost</c>, server-authentication EKU) and a client
/// certificate (CN <c>cohesion-client</c>, client-authentication EKU) for mutual TLS.
/// </summary>
public sealed class TestCertificateFixture : IDisposable
{
    private const string serverAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string clientAuthentication = "1.3.6.1.5.5.7.3.2";

    public TestCertificateFixture()
    {
        Certificate = CreateSelfSignedCertificate("localhost", serverAuthentication);
        ClientCertificate = CreateSelfSignedCertificate("cohesion-client", clientAuthentication);
    }

    public X509Certificate2 Certificate { get; }

    public X509Certificate2 ClientCertificate { get; }

    public void Dispose()
    {
        Certificate.Dispose();
        ClientCertificate.Dispose();
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string host, string extendedKeyUsage)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder sanBuilder = new();

        sanBuilder.AddDnsName(host);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(extendedKeyUsage)], critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        // Windows SChannel rejects ephemeral keys for server auth; round-trip through PFX so the
        // private key is loaded in a persistable form acceptable to all platforms.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}

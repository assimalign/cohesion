using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Security.Tests;

/// <summary>
/// An xUnit class fixture that creates a single ephemeral self-signed RSA certificate
/// (CN/SAN <c>localhost</c>, server-authentication EKU) shared by every test in the class.
/// </summary>
public sealed class TestCertificateFixture : IDisposable
{
    public TestCertificateFixture()
    {
        Certificate = CreateSelfSignedCertificate("localhost");
    }

    public X509Certificate2 Certificate { get; }

    public void Dispose() => Certificate.Dispose();

    private static X509Certificate2 CreateSelfSignedCertificate(string host)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder sanBuilder = new();

        sanBuilder.AddDnsName(host);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        // Windows SChannel rejects ephemeral keys for server auth; round-trip through PFX so the
        // private key is loaded in a persistable form acceptable to all platforms.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}

using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An xUnit class fixture that creates the ephemeral self-signed RSA server certificate the in-band TLS tests share,
/// for the host <see cref="HostName"/>, with the server-authentication extended key usage.
/// </summary>
public sealed class TestCertificateFixture : IDisposable
{
    /// <summary>
    /// The host the certificate names, which a client's TLS options name as their target host.
    /// </summary>
    public const string HostName = "amqp.test";

    private const string serverAuthentication = "1.3.6.1.5.5.7.3.1";

    public TestCertificateFixture()
    {
        Certificate = CreateSelfSignedCertificate();
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>
    /// Validates a server certificate the way a client that pins this certificate does: the certificate must be this
    /// one and name the host, and only its chain, which no authority signed, may fail.
    /// </summary>
    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        return certificate is not null
            && string.Equals(certificate.GetCertHashString(), Certificate.GetCertHashString(), StringComparison.Ordinal)
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
    }

    public void Dispose()
    {
        Certificate.Dispose();
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new($"CN={HostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();

        names.AddDnsName(HostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(serverAuthentication)], critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        // Windows SChannel refuses an ephemeral key for server authentication; a round trip through PFX loads the
        // private key in a form every platform accepts.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}

using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// Creates an ephemeral self-signed server-authentication certificate for the TLS convenience
/// integration tests, mirroring the pattern the <c>Http.Connections</c> HTTP/2 example uses so a real
/// TLS handshake succeeds over loopback on every supported platform.
/// </summary>
internal static class SelfSignedCertificateFactory
{
    /// <summary>
    /// Builds a self-signed certificate whose subject/SAN cover <paramref name="subjectName"/>,
    /// <c>localhost</c>, and the IPv4 loopback address, with the server-authentication EKU.
    /// </summary>
    /// <param name="subjectName">The certificate subject common name and primary DNS SAN.</param>
    /// <returns>A loaded <see cref="X509Certificate2"/> with an exportable private key.</returns>
    public static X509Certificate2 Create(string subjectName)
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            new X500DistinguishedName($"CN={subjectName}"),
            ecdsa,
            HashAlgorithmName.SHA256);

        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddDnsName(subjectName);
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1")
            },
            false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        // Round-trip through PKCS#12 so the private key is persisted in a form Windows Schannel
        // (SslStream) accepts; CreateSelfSigned alone yields an ephemeral key the platform TLS stack
        // rejects for server auth.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}

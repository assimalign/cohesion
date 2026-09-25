using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

internal sealed class TestTlsCertificate
{
    internal TestTlsCertificate(string host = "127.0.0.1")
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            names.AddIpAddress(address);
        }
        else
        {
            names.AddDnsName(host);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey,
            critical: false));
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        Thumbprint = certificate.Thumbprint;

        string pem = certificate.ExportCertificatePem() + '\n' + key.ExportPkcs8PrivateKeyPem();
        byte[] content = Encoding.UTF8.GetBytes(pem);
        try
        {
            Mount = ResourceMount.FromBytes(content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    internal ResourceMount Mount { get; }

    internal string Thumbprint { get; }
}

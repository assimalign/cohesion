using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Assimalign.Cohesion.Transports.Tests;

internal static class CertUtility
{
    public static X509Certificate2 GetSelfSignedCertificate()
    {
        X509Certificate2? certificate;

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

        if (!store.IsOpen)
        {
            store.Open(OpenFlags.ReadWrite);
        }

        certificate = store.Certificates.FirstOrDefault(p => p.Issuer == "CN=localhost" && p.FriendlyName == "");

        if (certificate is not null)
        {
            var now = DateTime.Now;

            if (certificate.NotAfter < now && certificate.NotBefore >= now)
            {
                return certificate;
            }
        }

        var timestamp = DateTimeOffset.UtcNow;
        var sanBuilder = new SubjectAlternativeNameBuilder();

        sanBuilder.AddDnsName("localhost");

        using var signature = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var certificateRequest = new CertificateRequest("CN=localhost", signature, HashAlgorithmName.SHA256);
        // Adds purpose
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
    {
        new("1.3.6.1.5.5.7.3.1") // serverAuth

    }, false));


        // Adds usage
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        // Adds subject alternate names
        certificateRequest.CertificateExtensions.Add(sanBuilder.Build());
        // Sign
        using var crt = certificateRequest.CreateSelfSigned(timestamp, timestamp.AddDays(365)); // 14 days is the max duration of a certificate for this

        certificate = X509CertificateLoader.LoadCertificate(crt.Export(X509ContentType.Pfx));

        // We need to add the certificate to the store so error is not thrown due to invalid cert chain
        store.Add(certificate);

        return certificate;
    }
}

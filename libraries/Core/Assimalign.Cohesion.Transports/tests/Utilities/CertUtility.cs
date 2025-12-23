using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Assimalign.Cohesion.Transports.Tests;

internal static class CertUtility
{
    private const string _thumbprint = "E661583E8FABEF4C0BEF694CBC41C28FB81CD870";

    public static X509Certificate2 GenerateSelfSignedCert()
    {
        X509Certificate2? certificate;

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

        if (!store.IsOpen)
        {
            store.Open(OpenFlags.ReadWrite);
        }

        certificate = store.Certificates
            .OfType<X509Certificate2>()
            .FirstOrDefault(p=>p.FriendlyName == "Cohesion Test Certificate");

        if (certificate is not null)
        {
            var now = DateTime.Now;

            if (certificate.NotAfter < now && certificate.NotBefore >= now)
            {
                return certificate;
            }
        }

        // Create RSA key pair
        using var rsa = RSA.Create(2048);

        // Define certificate subject
        var request = new CertificateRequest(
            $"CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add Subject Alternative Name (SAN) for IP
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
     
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Add Key Usage extension
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));

        // Add Public Key
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Create self-signed certificate
        certificate = request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));

#if IS_WINDOWS
#pragma warning disable
        certificate.FriendlyName = "Cohesion Test Certificate";
#endif
        // TODO: Fix export and load issue
        //certificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Pfx));

        // We need to add the certificate to the store so error is not thrown due to invalid cert chain
        store.Add(certificate);

        return certificate;
    }

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
        return certificateRequest.CreateSelfSigned(timestamp, timestamp.AddDays(365)); // 14 days is the max duration of a certificate for this

        // TODO: Fix export and load issue
        //certificate = X509CertificateLoader.LoadCertificate(crt.Export(X509ContentType.Pfx));

        //// We need to add the certificate to the store so error is not thrown due to invalid cert chain
        //store.Add(certificate);

        //return certificate;
    }
}

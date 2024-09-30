using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Cryptography;

public static class CertificateManagerExtensions
{
    //public static bool TryCreateOrGetDevelopmentCertificate(this ICertificateManager certificateManager, X509Certificate2 certificate)
    //{
    //    certificate = null;

    //    var certificateProvider = certificateManager.GetUserCertificateProvider("");


    //    certificateProvider.CreateSelfSignedCertificate()
    //    using (RSA rsa = RSA.Create(2048))
    //    {
    //        // Create a certificate request
    //        var certRequest = new CertificateRequest(
    //            certificateName,
    //            rsa,
    //            HashAlgorithmName.SHA256,
    //            RSASignaturePadding.Pkcs1);

    //        // Add extensions (optional)
    //        certRequest.CertificateExtensions.Add(
    //            new X509BasicConstraintsExtension(false, false, 0, false));
    //        certRequest.CertificateExtensions.Add(
    //            new X509KeyUsageExtension(
    //                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
    //                false));
    //        certRequest.CertificateExtensions.Add(
    //            new X509SubjectKeyIdentifierExtension(certRequest.PublicKey, false));

    //        // Self-sign the certificate
    //        certificate = certRequest.CreateSelfSigned(
    //            DateTimeOffset.Now,
    //            DateTimeOffset.Now.AddYears(1));

    //        // Export the certificate to PFX format
    //        byte[] pfxBytes = certificate.Export(X509ContentType.Pfx, "password");

    //        // Save the certificate to a file
    //        System.IO.File.WriteAllBytes("localhost.pfx", pfxBytes);

    //        Console.WriteLine("Self-signed certificate created successfully!");
    //    }
    //}
}

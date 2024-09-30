using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Net.Cryptography.Internal;

internal abstract class CertificateProviderBase : ICertificateProvider
{
    private const int CurrentCohesionNetCoreCertificateVersion = 2;
    private const string CohesionNetHttpsOid = "1.3.6.1.4.1.311.84.1.1";
    private const string CohesionNetHttpsOidFriendlyName = "Cohesion.Net Server HTTPS Development Certificate";
    private const string ServerAuthenticationEnhancedKeyUsageOid = "1.3.6.1.5.5.7.3.1";
    private const string ServerAuthenticationEnhancedKeyUsageOidFriendlyName = "Server Authentication";
    private const int    RSAMinimumKeySizeInBits = 2048;


    protected readonly X509Store store;


    public CertificateProviderBase(string storeName, StoreLocation storeLocation)
    {
        StoreName = storeName;
        StoreLocation = storeLocation.ToString();
        store = new X509Store(storeName, storeLocation, OpenFlags.MaxAllowed);
    }

    public virtual string StoreName { get; }
    public virtual string StoreLocation { get; }
    public virtual X509Store GetCertificateStore() => this.store;

    ICertificateResult ICertificateProvider.CreateSelfSignedCertificate(string certificateSubject, string certificateDnsName, string certificateOid, string certificateOidName) => CreateSelfSignedCertificate(certificateSubject, certificateDnsName, certificateOid, certificateOidName);
    public virtual CertificateResult CreateSelfSignedCertificate(string certificateSubject, string certificateDnsName, string certificateOid, string certificateOidName)
    {
        var extensions = new List<X509Extension>();
        var sanBuilder = new SubjectAlternativeNameBuilder();

        sanBuilder.AddDnsName(certificateDnsName);

        extensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection()
        {
            new Oid(ServerAuthenticationEnhancedKeyUsageOid, ServerAuthenticationEnhancedKeyUsageOidFriendlyName)
        }, critical: true));
        extensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        extensions.Add(new X509Extension(new AsnEncodedData(new Oid(certificateOid, certificateOidName), Encoding.ASCII.GetBytes(certificateOidName)), 
            critical: false));

        using var key = CreateKeyMaterial(RSAMinimumKeySizeInBits);

        var request = new CertificateRequest(new X500DistinguishedName(certificateSubject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(sanBuilder.Build(true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, true));

        var now = DateTimeOffset.UtcNow;
        var result = request.CreateSelfSigned(now, now.AddYears(1));
        return result;

        RSA CreateKeyMaterial(int minimumKeySize)
        {
            var rsa = RSA.Create(minimumKeySize);
            if (rsa.KeySize < minimumKeySize)
            {
                throw new InvalidOperationException($"Failed to create a key with a size of {minimumKeySize} bits");
            }

            return rsa;
        }
    }
   

    

    ICertificateResult ICertificateProvider.GetCertificate(string thumbprint) => GetCertificate(thumbprint);
    public virtual CertificateResult GetCertificate(string thumbprint)
    {
        foreach (var certificate in store.Certificates.Where(cert => cert.Thumbprint == thumbprint))
        {
            CertificateResult result = certificate;

            if (result.IsValid)
            {
                return result;
            }
        }

        throw new Exception("No valid certificate was found");
    }

    public abstract CertificateResult SaveCertificate(X509Certificate2 certificate);
    ICertificateResult ICertificateProvider.SaveCertificate(X509Certificate2 certificate) => this.SaveCertificate(certificate);

    public abstract CertificateResult UpdateCertificate(X509Certificate2 certificate);
    ICertificateResult ICertificateProvider.UpdateCertificate(X509Certificate2 certificate)=> this.UpdateCertificate(certificate);

    public abstract CertificateResult ExportCertificate(X509Certificate2 certificate, FilePath filePath);
    ICertificateResult ICertificateProvider.ExportCertificate(X509Certificate2 certificate, FilePath filePath) => ExportCertificate(certificate, filePath);

    public abstract CertificateResult ImportCertificate(FilePath filePath, string password);
    ICertificateResult ICertificateProvider.ImportCertificate(FilePath filePath, string password) => ImportCertificate(filePath, password);

    public virtual void Dispose()
    {
        if (store.IsOpen)
        {
            store.Close();
        }
        store.Dispose();
    }
}

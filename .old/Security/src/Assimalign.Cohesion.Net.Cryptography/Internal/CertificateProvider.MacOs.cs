using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Cryptography.Internal;

[SupportedOSPlatform("MacOs")]
internal sealed class MacOsCertificateProvider : CertificateProviderBase
{
    public MacOsCertificateProvider(string storeName, StoreLocation storeLocation) : base(storeName, storeLocation) { }

    public override CertificateResult ExportCertificate(X509Certificate2 certificate, FilePath filePath)
    {
        throw new NotImplementedException();
    }

   
    public override CertificateResult ImportCertificate(FilePath filePath, string password)
    {
        if (!File.Exists(filePath.ToString()))
        {
            throw new Exception();
        }

        var certificate = new X509Certificate2(
            filePath.ToString(), 
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        return certificate;
    }

    public override CertificateResult SaveCertificate(X509Certificate2 certificate)
    {
        throw new NotImplementedException();
    }

    public override CertificateResult UpdateCertificate(X509Certificate2 certificate)
    {
        throw new NotImplementedException();
    }
}

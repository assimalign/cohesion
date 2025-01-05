using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Cryptography.Internal;

internal readonly partial struct CertificateResult : ICertificateResult
{
    private bool IsWindowsCertificateTrusted()
    {
        throw new NotImplementedException();
    }
    private bool IsWindowsCertificateExportable()
    {
        using var privateKey = Certificate.GetRSAPrivateKey();

        return (privateKey is RSACryptoServiceProvider cryptoServiceProvider && cryptoServiceProvider.CspKeyContainerInfo.Exportable) ||
               (privateKey is RSACng cngPrivateKey && cngPrivateKey.Key.ExportPolicy == CngExportPolicies.AllowExport);
    }   
}
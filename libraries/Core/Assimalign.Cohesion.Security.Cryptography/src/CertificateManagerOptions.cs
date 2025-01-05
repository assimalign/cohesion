using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Cryptography;

public sealed class CertificateManagerOptions
{
    /// <summary>
    /// The object Identifier to be used when creating a certificate
    /// </summary>
    public string ObjectId { get; set; } = "1.3.6.1.4.1.311.84.1.1";
    /// <summary>
    /// 
    /// </summary>
    public string ObjectFriendlyName { get; set; } = "Cohesion.Net Server Development Certificate";
    /// <summary>
    /// 
    /// </summary>
    public string EnhancedKeyUsageOid { get; set; } = "1.3.6.1.5.5.7.3.1";
    /// <summary>
    /// 
    /// </summary>
    public string EnhancedKeyUsageOidFriendlyName { get; set; } = "Server Authentication";
}

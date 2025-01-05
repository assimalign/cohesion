using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Net.Cryptography;

public interface ICertificateResult
{
    /// <summary>
    /// 
    /// </summary>
    X509Certificate2 Certificate { get; }
    /// <summary>
    /// 
    /// </summary>
    bool IsExportable { get; }
    /// <summary>
    /// 
    /// </summary>
    bool IsTrusted { get; }
    /// <summary>
    /// 
    /// </summary>
    bool IsActive { get; }
    /// <summary>
    /// 
    /// </summary>
    bool IsExpired { get; }
    /// <summary>
    /// A certificate is valid when it hasn't expired, has a private key and its exportable.
    /// </summary>
    bool IsValid { get; }
}

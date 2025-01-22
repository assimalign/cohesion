using System;
using System.Security;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Net.Cryptography;

/// <summary>
/// A Certificate Provider is an implementation scoped to a particular 
/// certificate store and location
/// </summary>
public interface ICertificateProvider : IDisposable
{
    /// <summary>
    /// Specifies the store the certificate lives in.
    /// </summary>
    string StoreName { get; }
    /// <summary>
    /// Specifies the location of the certificate within the certificate store.
    /// </summary>
    string StoreLocation { get; }
    /// <summary>
    /// Get's the current certificate store.
    /// </summary>
    /// <returns></returns>
    X509Store GetCertificateStore();
    /// <summary>
    /// Create a self-signed certificate.
    /// </summary>
    /// <returns></returns>
    ICertificateResult CreateSelfSignedCertificate(string certificateSubject, string certificateDnsName, string certificateOid, string certificateOidName);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="thumbprint"></param>
    /// <returns></returns>
    ICertificateResult GetCertificate(string thumbprint);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="certificate"></param>
    /// <returns></returns>
    ICertificateResult SaveCertificate(X509Certificate2 certificate);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="certificate"></param>
    /// <returns></returns>
    ICertificateResult UpdateCertificate(X509Certificate2 certificate);    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="certificate"></param>
    /// <returns></returns>
    ICertificateResult ImportCertificate(FilePath path, string password);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="certificate"></param>
    /// <returns></returns>
    ICertificateResult ExportCertificate(X509Certificate2 certificate, FilePath filePath);
}
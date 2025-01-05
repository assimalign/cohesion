using System;

namespace Assimalign.Cohesion.Net.Cryptography;

public interface ICertificateManager
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="storeName"></param>
    /// <returns></returns>
    ICertificateProvider GetMachineCertificateProvider(string storeName);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="storeName"></param>
    /// <returns></returns>
    ICertificateProvider GetUserCertificateProvider(string storeName);
}
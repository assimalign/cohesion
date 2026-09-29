using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

internal sealed class CertificateFixture : IDisposable
{
    internal ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal X509Certificate2 Root { get; }
    internal X509Certificate2 Leaf { get; }

    internal CertificateFixture()
    {
        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256);
        root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Root = root.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        var request = new CertificateRequest("CN=localhost", Key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        Leaf = request.Create(Root, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
    }

    internal string Bundle(string order) => order switch
    {
        "store" => Leaf.ExportCertificatePem() + "\n" + Key.ExportPkcs8PrivateKeyPem() + "\n" + Root.ExportCertificatePem(),
        "gateway" => Leaf.ExportCertificatePem() + "\n" + Root.ExportCertificatePem() + "\n" + Key.ExportPkcs8PrivateKeyPem(),
        _ => Leaf.ExportCertificatePem() + "\n" + Key.ExportPkcs8PrivateKeyPem(),
    };

    public void Dispose()
    {
        Leaf.Dispose();
        Root.Dispose();
        Key.Dispose();
    }
}

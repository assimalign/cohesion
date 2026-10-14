using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// Writes one self-signed server certificate to a temporary directory in every file form the HTTP
/// configuration binder loads: a PEM certificate with a separate PEM key (plain and encrypted), a PEM
/// bundle carrying both, and a password-protected PKCS#12 (PFX) file. Disposing deletes the directory.
/// </summary>
internal sealed class CertificateFiles : IDisposable
{
    /// <summary>
    /// The password protecting the PFX file and the encrypted PEM key.
    /// </summary>
    public const string Password = "cohesion-test";

    private CertificateFiles(string directory, X509Certificate2 certificate)
    {
        Directory = directory;
        Certificate = certificate;
        PemCertificatePath = Path.Combine(directory, "server.crt");
        PemKeyPath = Path.Combine(directory, "server.key");
        EncryptedPemKeyPath = Path.Combine(directory, "server-encrypted.key");
        PemBundlePath = Path.Combine(directory, "server.pem");
        PfxPath = Path.Combine(directory, "server.pfx");
    }

    /// <summary>Gets the directory holding the files (use it as the content root).</summary>
    public string Directory { get; }

    /// <summary>Gets the certificate (public part only) every file carries.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Gets the path of the PEM certificate.</summary>
    public string PemCertificatePath { get; }

    /// <summary>Gets the path of the unencrypted PKCS#8 PEM key.</summary>
    public string PemKeyPath { get; }

    /// <summary>Gets the path of the PKCS#8 PEM key encrypted with <see cref="Password"/>.</summary>
    public string EncryptedPemKeyPath { get; }

    /// <summary>Gets the path of the PEM file carrying the certificate and its key.</summary>
    public string PemBundlePath { get; }

    /// <summary>Gets the path of the PKCS#12 file protected with <see cref="Password"/>.</summary>
    public string PfxPath { get; }

    /// <summary>
    /// Creates the certificate and writes every file form.
    /// </summary>
    /// <returns>The written files.</returns>
    public static CertificateFiles Create()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cohesion-certs-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        CertificateFiles files = new(directory, X509CertificateLoader.LoadCertificate(ephemeral.RawData));

        string certificatePem = ephemeral.ExportCertificatePem();
        string keyPem = key.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(files.PemCertificatePath, certificatePem);
        File.WriteAllText(files.PemKeyPath, keyPem);
        File.WriteAllText(
            files.EncryptedPemKeyPath,
            key.ExportEncryptedPkcs8PrivateKeyPem(
                Password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, iterationCount: 10_000)));
        File.WriteAllText(files.PemBundlePath, certificatePem + "\n" + keyPem);
        File.WriteAllBytes(files.PfxPath, ephemeral.Export(X509ContentType.Pfx, Password));

        return files;
    }

    public void Dispose()
    {
        Certificate.Dispose();

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temporary directory left behind does not affect the test's verdict.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

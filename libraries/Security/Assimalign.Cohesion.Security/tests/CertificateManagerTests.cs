using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Security.Tests;

public class CertificateManagerTests
{
    private const string Subject = "CN=cohesion-cert-test";
    private const string Pkcs12Password = "p@ssw0rd";

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: Loads a PKCS#12 certificate from a file")]
    public void LoadPkcs12FromFile_OnValidPfx_ReturnsCertificateWithPrivateKey()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();
        using TempFile pfx = TempFile.Write(CreateSelfSignedPfx(Pkcs12Password), ".pfx");

        using X509Certificate2 certificate = manager.LoadPkcs12FromFile(pfx.Path, Pkcs12Password);

        certificate.Subject.ShouldBe(Subject);
        certificate.HasPrivateKey.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: Loads a PKCS#12 certificate from bytes")]
    public void LoadPkcs12_OnValidBytes_ReturnsCertificate()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();
        byte[] pfx = CreateSelfSignedPfx(Pkcs12Password);

        using X509Certificate2 certificate = manager.LoadPkcs12(pfx, Pkcs12Password);

        certificate.Subject.ShouldBe(Subject);
    }

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: Loads a PEM certificate paired with a private key")]
    public void LoadPemFromFile_OnCertAndKey_ReturnsCertificateWithPrivateKey()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();
        using ECDsa key = ECDsa.Create();
        using X509Certificate2 source = CreateSelfSigned(key);
        using TempFile certPem = TempFile.Write(source.ExportCertificatePem(), ".crt");
        using TempFile keyPem = TempFile.Write(key.ExportPkcs8PrivateKeyPem(), ".key");

        using X509Certificate2 certificate = manager.LoadPemFromFile(certPem.Path, keyPem.Path);

        certificate.Subject.ShouldBe(Subject);
        certificate.HasPrivateKey.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: Loads a PEM certificate without a private key")]
    public void LoadPemFromFile_OnCertOnly_ReturnsCertificate()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();
        using ECDsa key = ECDsa.Create();
        using X509Certificate2 source = CreateSelfSigned(key);
        using TempFile certPem = TempFile.Write(source.ExportCertificatePem(), ".crt");

        using X509Certificate2 certificate = manager.LoadPemFromFile(certPem.Path);

        certificate.Subject.ShouldBe(Subject);
        certificate.HasPrivateKey.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: A missing PKCS#12 file throws CertificateException")]
    public void LoadPkcs12FromFile_OnMissingFile_ThrowsCertificateException()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();
        string missing = Path.Combine(Path.GetTempPath(), $"cohesion-missing-{Guid.NewGuid():N}.pfx");

        Should.Throw<CertificateException>(() => manager.LoadPkcs12FromFile(missing, Pkcs12Password));
    }

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: An empty path throws ArgumentException")]
    public void LoadPkcs12FromFile_OnEmptyPath_ThrowsArgumentException()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();

        Should.Throw<ArgumentException>(() => manager.LoadPkcs12FromFile(string.Empty, Pkcs12Password));
    }

    [Fact(DisplayName = "Cohesion Test [Security] - CertificateManager: Empty PKCS#12 data throws ArgumentException")]
    public void LoadPkcs12_OnEmptyData_ThrowsArgumentException()
    {
        ICertificateManager manager = CertificateManagerFactory.Create();

        Should.Throw<ArgumentException>(() => manager.LoadPkcs12(ReadOnlyMemory<byte>.Empty, Pkcs12Password));
    }

    private static byte[] CreateSelfSignedPfx(string password)
    {
        using ECDsa key = ECDsa.Create();
        using X509Certificate2 certificate = CreateSelfSigned(key);
        return certificate.Export(X509ContentType.Pkcs12, password);
    }

    private static X509Certificate2 CreateSelfSigned(ECDsa key)
    {
        CertificateRequest request = new(Subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path) => Path = path;

        public string Path { get; }

        public static TempFile Write(byte[] content, string extension)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cohesion-cert-{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(path, content);
            return new TempFile(path);
        }

        public static TempFile Write(string content, string extension)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cohesion-cert-{Guid.NewGuid():N}{extension}");
            File.WriteAllText(path, content);
            return new TempFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp file is harmless.
            }
        }
    }
}

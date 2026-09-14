using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public sealed class EndpointCertificateTests
{
    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Certificate: Accepts legacy EC and RSA private-key labels")]
    [InlineData(false)]
    [InlineData(true)]
    public void TryGetEndpointCertificate_LegacyKeyLabels_ShouldPreserveIdentity(bool rsa)
    {
        using ECDsa ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA rsaKey = RSA.Create(2048);
        CertificateRequest request = rsa
            ? new CertificateRequest("CN=localhost", rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest("CN=localhost", ecKey, HashAlgorithmName.SHA256);
        using X509Certificate2 expected = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string key = rsa ? rsaKey.ExportRSAPrivateKeyPem() : ecKey.ExportECPrivateKeyPem();
        var context = new ResourceContext(mounts: new Dictionary<string, ResourceMount>
        {
            ["tls"] = ResourceMount.FromBytes(Encoding.UTF8.GetBytes(expected.ExportCertificatePem() + "\n" + key)),
        });
        context.TryGetEndpointCertificate("api", out X509Certificate2? actual).ShouldBeTrue();
        using (actual)
        {
            actual.Thumbprint.ShouldBe(expected.Thumbprint);
            actual.HasPrivateKey.ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Certificate: Preserves the three existing PEM bundle shapes")]
    [InlineData("store")]
    [InlineData("gateway")]
    [InlineData("leaf-only")]
    public void TryGetEndpointCertificate_ProducerOrders_ShouldPreserveLeafAndChain(string order)
    {
        using var fixture = new CertificateFixture();
        string pem = fixture.Bundle(order);
        var context = new ResourceContext(mounts: new Dictionary<string, ResourceMount> { ["tls"] = ResourceMount.FromBytes(Encoding.UTF8.GetBytes(pem)) });
        using X509Certificate2 legacyLeaf = X509Certificate2.CreateFromPem(pem, pem);
        var legacyChain = new X509Certificate2Collection();
        legacyChain.ImportFromPem(pem);
        try
        {
            context.TryGetEndpointCertificate("https", out X509Certificate2? leaf, out X509Certificate2Collection chain).ShouldBeTrue();
            using (leaf)
            {
                leaf.Thumbprint.ShouldBe(legacyLeaf.Thumbprint);
                leaf.HasPrivateKey.ShouldBeTrue();
                chain.Count.ShouldBe(legacyChain.Count - 1);
                if (chain.Count > 0)
                {
                    chain[0].Thumbprint.ShouldBe(fixture.Root.Thumbprint);
                }
                SslStreamCertificateContext.Create(leaf, chain, offline: true).TargetCertificate.Thumbprint.ShouldBe(legacyLeaf.Thumbprint);
                foreach (X509Certificate2 issuer in chain)
                {
                    issuer.Dispose();
                }
            }
        }
        finally
        {
            foreach (X509Certificate2 certificate in legacyChain)
            {
                certificate.Dispose();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Certificate: Absent and empty mount files return false")]
    public void TryGetEndpointCertificate_AbsentOrEmpty_ShouldReturnFalse()
    {
        new ResourceContext().TryGetEndpointCertificate("api", out X509Certificate2? absent).ShouldBeFalse();
        absent.ShouldBeNull();
        string path = Path.Combine(AppContext.BaseDirectory, "empty-cert-" + Guid.NewGuid().ToString("N"));
        byte[] empty = Array.Empty<byte>();
        if (OperatingSystem.IsWindows())
        {
            empty = ProtectedData.Protect(empty, null, DataProtectionScope.CurrentUser);
        }

        File.WriteAllBytes(path, empty);
        try
        {
            var context = new ResourceContext(mounts: new Dictionary<string, ResourceMount> { ["tls"] = new ResourceMount(path) });
            context.TryGetEndpointCertificate("api", out X509Certificate2? certificate, out X509Certificate2Collection chain).ShouldBeFalse();
            certificate.ShouldBeNull();
            chain.ShouldBeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Certificate: Rejects missing, duplicate and malformed key material")]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void TryGetEndpointCertificate_UnusableBundle_ShouldThrowNamedError(int keys)
    {
        using var fixture = new CertificateFixture();
        string pem = keys == -1 ? "not a PEM document" : fixture.Leaf.ExportCertificatePem();
        for (int index = 0; index < keys; index++)
        {
            pem += fixture.Key.ExportPkcs8PrivateKeyPem();
        }

        var context = new ResourceContext(mounts: new Dictionary<string, ResourceMount> { ["tls"] = ResourceMount.FromBytes(Encoding.UTF8.GetBytes(pem)) });
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => context.TryGetEndpointCertificate("api", out X509Certificate2? _));
        exception.Message.ShouldContain("api");
        exception.Message.ShouldContain("tls");
        exception.Message.ShouldContain("exactly one");
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Certificate: Resolves mapped punctuation through the protected single-file carrier")]
    public void TryGetEndpointCertificate_NormalizedMount_ShouldReadProtectedFile()
    {
        using var fixture = new CertificateFixture();
        string path = Path.Combine(AppContext.BaseDirectory, "mapped-cert-" + Guid.NewGuid().ToString("N"));
        byte[] pem = Encoding.UTF8.GetBytes(fixture.Bundle("store"));
        byte[] stored = OperatingSystem.IsWindows() ? ProtectedData.Protect(pem, null, DataProtectionScope.CurrentUser) : pem;
        File.WriteAllBytes(path, stored);
        try
        {
            var context = new ResourceContext("app", "api", "Development", "local", null, null, null, null, null,
                default, default, new Dictionary<string, string?> { [ResourceEnvironment.Mount("api-tls.pem")] = path },
                new Dictionary<string, string> { ["https"] = "api-tls.pem" });
            context.TryGetEndpointCertificate("https", out X509Certificate2? certificate).ShouldBeTrue();
            using (certificate)
            {
                certificate.Thumbprint.ShouldBe(fixture.Leaf.Thumbprint);
            }
        }
        finally
        {
            File.Delete(path);
            CryptographicOperations.ZeroMemory(pem);
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Trust: Validates the application chain and rejects unrelated roots and hostname mismatch")]
    public void CreateOutboundTrustValidator_CustomRoot_ShouldPreserveAuthentication()
    {
        using var fixture = new CertificateFixture();
        using var unrelated = new CertificateFixture();
        string path = Path.Combine(AppContext.BaseDirectory, "trust-" + Guid.NewGuid().ToString("N"));
        byte[] pem = Encoding.UTF8.GetBytes(fixture.Root.ExportCertificatePem());
        File.WriteAllBytes(path, OperatingSystem.IsWindows() ? ProtectedData.Protect(pem, null, DataProtectionScope.CurrentUser) : pem);
        try
        {
            ResourceContext context = ResourceContext.FromEnvironment(new Dictionary<string, string?> { [ResourceEnvironment.TrustBundlePath] = path });
            context.TryGetTrustBundle(out X509Certificate2Collection anchors).ShouldBeTrue();
            anchors.Count.ShouldBe(1);
            anchors[0].HasPrivateKey.ShouldBeFalse();
            anchors[0].Dispose();
            RemoteCertificateValidationCallback validator = context.CreateOutboundTrustValidator().ShouldNotBeNull();
            validator(this, fixture.Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
            validator(this, unrelated.Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
            validator(this, fixture.Leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeFalse();
            validator(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable).ShouldBeFalse();
        }
        finally { File.Delete(path); }
    }

}

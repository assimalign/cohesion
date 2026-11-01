using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.SecretStore.Hosting.Internal;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class CertificateAuthorityManagerTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Certificate authority: self-seeded authority and leaf survive restart with a valid chain")]
    public async Task InitializeAsync_WithExistingDurableState_ShouldRetainSelfSeededAuthorityAndLeafChain()
    {
        // Arrange
        string dataPath = SecretStoreTestHost.CreateTemporaryDirectory();
        try
        {
            IDataProtectionProvider firstProvider = CreateProtectionProvider(dataPath);
            string firstRoot;
            string firstLeaf;
            using (var first = new CertificateAuthorityManager(
                dataPath,
                firstProvider,
                new CertificateAuthorityOptions()))
            {
                using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await first.InitializeAsync(cancellationTokenSource.Token);
                firstRoot = await first.GetCertificatePemAsync("ca/root", cancellationTokenSource.Token);
                firstLeaf = await first.GetCertificatePemAsync("certs/appa-api", cancellationTokenSource.Token);
            }

            // Act
            IDataProtectionProvider secondProvider = CreateProtectionProvider(dataPath);
            string secondRoot;
            string secondLeaf;
            using (var second = new CertificateAuthorityManager(
                dataPath,
                secondProvider,
                new CertificateAuthorityOptions()))
            {
                using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await second.InitializeAsync(cancellationTokenSource.Token);
                secondRoot = await second.GetCertificatePemAsync("ca/root", cancellationTokenSource.Token);
                secondLeaf = await second.GetCertificatePemAsync("certs/appa-api", cancellationTokenSource.Token);
            }

            // Assert
            secondRoot.ShouldBe(firstRoot);
            secondLeaf.ShouldBe(firstLeaf);
            using X509Certificate2 root = X509Certificate2.CreateFromPem(secondRoot);
            X509Certificate2Collection bundle = LoadCertificates(secondLeaf);
            try
            {
                bundle.Count.ShouldBe(2);
                X509Certificate2 leaf = bundle.Single(certificate => !IsCertificateAuthority(certificate));
                BuildChain(leaf, root, []).ShouldBeTrue();
            }
            finally
            {
                DisposeCertificates(bundle);
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static IDataProtectionProvider CreateProtectionProvider(string dataPath)
    {
        return DataProtectionProvider.Create(
            FileSystemKeyRepository.Create(Path.Combine(dataPath, "test-key-ring")),
            options =>
            {
                options.ApplicationDiscriminator = "SecretStore.Hosting.Tests";
                options.KeyLifetime = TimeSpan.FromDays(36500);
                options.UnprotectGracePeriod = TimeSpan.FromDays(36500);
            });
    }

    private static bool BuildChain(
        X509Certificate2 leaf,
        X509Certificate2 root,
        X509Certificate2[] intermediates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        return chain.Build(leaf);
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate)
        => certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Single()
            .CertificateAuthority;

    private static X509Certificate2Collection LoadCertificates(string pem)
    {
        const string beginMarker = "-----BEGIN CERTIFICATE-----";
        const string endMarker = "-----END CERTIFICATE-----";

        var certificates = new X509Certificate2Collection();
        var offset = 0;
        while (true)
        {
            int begin = pem.IndexOf(beginMarker, offset, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            int end = pem.IndexOf(endMarker, begin + beginMarker.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException("A certificate PEM block is not terminated.");
            }

            int length = end + endMarker.Length - begin;
            certificates.Add(X509Certificate2.CreateFromPem(pem.AsSpan(begin, length)));
            offset = end + endMarker.Length;
        }

        return certificates;
    }

    private static void DisposeCertificates(X509Certificate2Collection certificates)
    {
        foreach (X509Certificate2 certificate in certificates)
        {
            certificate.Dispose();
        }
    }
}

using Assimalign.Cohesion.SecretStore.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.SecretStore.Tests;

public sealed class CertificateAuthorityOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore] - CertificateAuthorityOptions: defaults to self-seeding")]
    public void CertificateAuthorityOptions_WhenCreated_ShouldDefaultToSelfSeeding()
    {
        // Arrange and Act
        CertificateAuthorityOptions options = new();

        // Assert
        options.CommonName.ShouldBe("Cohesion SecretStore Certificate Authority");
        options.SelfSeed.ShouldBeTrue();
        options.InitialCertificate.ShouldBeNull();
        options.InitialPrivateKey.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - Builder declarations: compose through the root contract")]
    public void AddSecretAndCertificateAuthority_WithRootBuilder_ShouldCaptureDeclarativeInputs()
    {
        // Arrange
        byte[] secret = [1, 2, 3];
        byte[] certificate = [4, 5, 6];
        byte[] privateKey = [7, 8];
        RecordingSecretStoreApplicationBuilder builder = new();

        // Act
        ISecretStoreApplicationBuilder returned = builder
            .AddSecret("apps/api/client-secret", secret)
            .AddCertificateAuthority(options =>
            {
                options.CommonName = "AppA Root CA";
                options.SelfSeed = false;
                options.InitialCertificate = certificate;
                options.InitialPrivateKey = privateKey;
            });

        secret[0] = 9;

        // Assert
        returned.ShouldBeSameAs(builder);
        builder.Secrets.Count.ShouldBe(1);
        builder.Secrets[0].Path.ShouldBe("apps/api/client-secret");
        builder.Secrets[0].Value.ShouldBe([1, 2, 3]);
        CertificateAuthorityOptions options = builder.CertificateAuthority.ShouldNotBeNull();
        options.CommonName.ShouldBe("AppA Root CA");
        options.SelfSeed.ShouldBeFalse();
        options.InitialCertificate.ShouldNotBeNull();
        options.InitialCertificate.Value.ToArray().ShouldBe([4, 5, 6]);
        options.InitialPrivateKey.ShouldNotBeNull();
        options.InitialPrivateKey.Value.ToArray().ShouldBe([7, 8]);
    }
}

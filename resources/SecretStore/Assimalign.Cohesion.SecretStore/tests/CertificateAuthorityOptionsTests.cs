using System;

using Assimalign.Cohesion.SecretStore.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.SecretStore.Tests;

public sealed class CertificateAuthorityOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore] - CertificateAuthorityOptions: defaults to standalone self-seeding")]
    public void CertificateAuthorityOptions_WhenCreated_ShouldDefaultToStandaloneSelfSeeding()
    {
        // Arrange and Act
        CertificateAuthorityOptions options = new();

        // Assert
        options.CommonName.ShouldBe("Cohesion SecretStore Certificate Authority");
        options.SelfSeedWhenNoPlatform.ShouldBeTrue();
        options.PlatformEnrollmentEndpoint.ShouldBeNull();
        options.PlatformCertificate.ShouldBeNull();
        options.InitialCertificate.ShouldBeNull();
        options.InitialPrivateKey.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - Builder declarations: compose through the root contract")]
    public void AddSecretAndCertificateAuthority_WithRootBuilder_ShouldCaptureDeclarativeInputs()
    {
        // Arrange
        byte[] secret = [1, 2, 3];
        byte[] platformCertificate = [4, 5, 6];
        Uri enrollmentEndpoint = new("https://platform.example.test/secrets/v1/enroll");
        RecordingSecretStoreApplicationBuilder builder = new();

        // Act
        ISecretStoreApplicationBuilder returned = builder
            .AddSecret("apps/api/client-secret", secret)
            .AddCertificateAuthority(options =>
            {
                options.CommonName = "AppA Intermediate CA";
                options.SelfSeedWhenNoPlatform = false;
                options.PlatformEnrollmentEndpoint = enrollmentEndpoint;
                options.PlatformCertificate = platformCertificate;
            });

        secret[0] = 9;

        // Assert
        returned.ShouldBeSameAs(builder);
        builder.Secrets.Count.ShouldBe(1);
        builder.Secrets[0].Path.ShouldBe("apps/api/client-secret");
        builder.Secrets[0].Value.ShouldBe([1, 2, 3]);
        CertificateAuthorityOptions options = builder.CertificateAuthority.ShouldNotBeNull();
        options.CommonName.ShouldBe("AppA Intermediate CA");
        options.SelfSeedWhenNoPlatform.ShouldBeFalse();
        options.PlatformEnrollmentEndpoint.ShouldBe(enrollmentEndpoint);
        options.PlatformCertificate.ShouldNotBeNull();
        options.PlatformCertificate.Value.ToArray().ShouldBe([4, 5, 6]);
    }
}

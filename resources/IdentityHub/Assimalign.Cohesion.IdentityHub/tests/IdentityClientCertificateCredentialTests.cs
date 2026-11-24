using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A certificate credential keeps the public certificate only; its window is the certificate's.
/// </summary>
public class IdentityClientCertificateCredentialTests
{
    private static readonly DateTimeOffset _now = TestClients.Now;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep a DER certificate, its thumbprint, and its validity period")]
    public void Create_WithADerCertificate_ShouldKeepTheCertificateAndItsValidity()
    {
        // Arrange
        using X509Certificate2 certificate = TestClients.Certificate(_now.AddDays(-1), _now.AddDays(365));

        // Act
        IdentityClientCertificateCredential credential =
            IdentityClientCertificateCredential.Create("signing", certificate.RawData, "Signing");

        // Assert
        credential.Id.Value.ShouldBe("signing");
        credential.DisplayName.ShouldBe("Signing");
        credential.Kind.ShouldBe(IdentityCredentialKind.Certificate);
        credential.Certificate.ToArray().ShouldBe(certificate.RawData);
        credential.Thumbprint.ShouldBe(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        credential.Thumbprint.Length.ShouldBe(64);
        credential.NotBefore.ShouldBe(_now.AddDays(-1));
        credential.ExpiresAt.ShouldBe(_now.AddDays(365));
        credential.IsUsable(_now).ShouldBeTrue();
        credential.IsUsable(_now.AddDays(-2)).ShouldBeFalse();
        credential.IsUsable(_now.AddDays(366)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should read a PEM certificate as the same credential")]
    public void Create_WithAPemCertificate_ShouldKeepTheDerEncoding()
    {
        // Arrange
        using X509Certificate2 certificate = TestClients.Certificate(_now, _now.AddDays(30));
        byte[] pem = Encoding.ASCII.GetBytes(certificate.ExportCertificatePem());

        // Act
        IdentityClientCertificateCredential credential = IdentityClientCertificateCredential.Create("signing", pem);

        // Assert
        credential.Certificate.ToArray().ShouldBe(certificate.RawData);
        credential.Thumbprint.ShouldBe(certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should copy the certificate")]
    public void Create_ThenChangingTheSource_ShouldNotChangeTheCredential()
    {
        // Arrange
        using X509Certificate2 certificate = TestClients.Certificate(_now, _now.AddDays(30));
        byte[] source = certificate.RawData;
        byte[] expected = (byte[])source.Clone();
        IdentityClientCertificateCredential credential = IdentityClientCertificateCredential.Create("signing", source);

        // Act
        Array.Clear(source);

        // Assert
        credential.Certificate.ToArray().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a store that carries a private key")]
    public void Create_WithAPkcs12Store_ShouldThrowArgumentException()
    {
        // Arrange
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=orders-daemon", key, HashAlgorithmName.SHA256);
        using X509Certificate2 withKey = request.CreateSelfSigned(_now, _now.AddDays(30));
        byte[] store = withKey.Export(X509ContentType.Pkcs12);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create("signing", store));

        // Assert
        exception.ParamName.ShouldBe("certificate");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse empty bytes, bytes that are no certificate, and input beyond the limit")]
    public void Create_WithBytesThatAreNoCertificate_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException empty = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create("signing", ReadOnlyMemory<byte>.Empty));
        ArgumentException garbage = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create("signing", new byte[] { 1, 2, 3, 4 }));
        ArgumentException oversized = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create(
                "signing",
                new byte[(IdentityClientCertificateCredential.MaxCertificateLength * 2) + 1]));

        // Assert
        empty.ParamName.ShouldBe("certificate");
        garbage.ParamName.ShouldBe("certificate");
        garbage.InnerException.ShouldBeAssignableTo<CryptographicException>();
        oversized.ParamName.ShouldBe("certificate");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a certificate larger than the limit")]
    public void Create_WithACertificateLargerThanTheLimit_ShouldThrowArgumentException()
    {
        // Arrange: an extension pads the certificate past the limit while the input stays readable.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=orders-daemon", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension(
            "1.3.6.1.4.1.55555.1",
            new byte[IdentityClientCertificateCredential.MaxCertificateLength + 512],
            critical: false));
        using X509Certificate2 large = request.CreateSelfSigned(_now, _now.AddDays(30));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create("signing", large.RawData));

        // Assert
        IdentityClientCertificateCredential.MaxCertificateLength.ShouldBe(8192);
        large.RawData.Length.ShouldBeGreaterThan(IdentityClientCertificateCredential.MaxCertificateLength);
        large.RawData.Length.ShouldBeLessThanOrEqualTo(IdentityClientCertificateCredential.MaxCertificateLength * 2);
        exception.ParamName.ShouldBe("certificate");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a default identifier and a display name outside its rule")]
    public void Create_WithAnInvalidIdentifierOrDisplayName_ShouldThrowArgumentException()
    {
        // Arrange
        using X509Certificate2 certificate = TestClients.Certificate(_now, _now.AddDays(30));
        byte[] der = certificate.RawData;

        // Act
        ArgumentException noId = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create(default, der));
        ArgumentException displayName = Should.Throw<ArgumentException>(
            () => IdentityClientCertificateCredential.Create("signing", der, "tab\tname"));

        // Assert
        noId.ParamName.ShouldBe("id");
        displayName.ParamName.ShouldBe("displayName");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Restore: Should rebuild the same credential from the stored certificate")]
    public void Restore_WithAStoredCertificate_ShouldRebuildTheSameCredential()
    {
        // Arrange
        using X509Certificate2 certificate = TestClients.Certificate(_now, _now.AddDays(30));
        IdentityClientCertificateCredential original =
            IdentityClientCertificateCredential.Create("signing", certificate.RawData, "Signing");

        // Act
        IdentityClientCertificateCredential restored = IdentityClientCertificateCredential.Restore(
            original.Id,
            original.DisplayName,
            original.NotBefore,
            original.ExpiresAt,
            original.Certificate.ToArray());

        // Assert
        restored.Certificate.ToArray().ShouldBe(original.Certificate.ToArray());
        restored.Thumbprint.ShouldBe(original.Thumbprint);
        restored.NotBefore.ShouldBe(original.NotBefore);
        restored.ExpiresAt.ShouldBe(original.ExpiresAt);
        restored.DisplayName.ShouldBe("Signing");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Restore: Should refuse a stored certificate this type does not write")]
    public void Restore_WithAStoredCertificateThisTypeDoesNotWrite_ShouldThrowFormatException()
    {
        // Arrange
        byte[] certificate = new byte[64];
        byte[] oversized = new byte[IdentityClientCertificateCredential.MaxCertificateLength + 1];

        // Act + Assert
        Should.Throw<FormatException>(() => IdentityClientCertificateCredential.Restore("signing", null, _now, _now.AddDays(1), null));
        Should.Throw<FormatException>(() => IdentityClientCertificateCredential.Restore("signing", null, _now, _now.AddDays(1), []));
        Should.Throw<FormatException>(() => IdentityClientCertificateCredential.Restore("signing", null, _now, _now.AddDays(1), oversized));
        Should.Throw<FormatException>(() => IdentityClientCertificateCredential.Restore("signing", null, null, _now.AddDays(1), certificate));
        Should.Throw<FormatException>(() => IdentityClientCertificateCredential.Restore("signing", null, _now, null, certificate));
    }
}

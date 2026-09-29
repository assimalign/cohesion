using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

/// <summary>
/// Verifies the gateway control plane's developer and peer credential rules on the shared ES256 validator:
/// trusted issuer, <c>cohesion-export</c> audience, the eight-hour ceiling, and the gateway token-use claim.
/// </summary>
public sealed class ControlPlaneTokenVerifierTests : IDisposable
{
    private const string displayPrefix = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - ControlPlaneTokenVerifier: ";
    private const string issuerName = "appb";
    private static readonly DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;
    private readonly TrustedIssuer _issuer;

    public ControlPlaneTokenVerifierTests()
    {
        ECParameters parameters = _signingKey.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X!);
        string y = Base64Url.EncodeToString(parameters.Q.Y!);
        _keyId = Base64Url.EncodeToString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}")));
        using JsonDocument publicKey = JsonDocument.Parse(
            $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{x}\",\"y\":\"{y}\",\"kid\":\"{_keyId}\",\"alg\":\"ES256\",\"use\":\"sig\"}}");
        _issuer = new TrustedIssuer(issuerName, publicKey.RootElement, ["test.command"]);
    }

    public void Dispose() => _signingKey.Dispose();

    [Fact(DisplayName = displayPrefix + "A trusted gateway credential maps to a command-capable principal")]
    public void TryVerify_WhenGatewayCredentialIsTrusted_ReturnsPrincipal()
    {
        // Arrange
        string token = Issue(TimeSpan.FromHours(1), gatewayUse: true);

        // Act
        bool verified = ControlPlaneTokenVerifier.TryVerify(token, [_issuer], _now, out ControlPlanePrincipal principal);

        // Assert
        verified.ShouldBeTrue();
        principal.Issuer.ShouldBe(issuerName);
        principal.Subject.ShouldBe("gateway");
        principal.CanDispatchCommands.ShouldBeTrue();
        principal.AllowedCommandKinds.ShouldBe(new[] { "test.command" });
    }

    [Fact(DisplayName = displayPrefix + "A developer credential without the gateway token use cannot dispatch commands")]
    public void TryVerify_WhenTokenUseIsAbsent_ReturnsReadOnlyPrincipal()
    {
        // Arrange
        string token = Issue(TimeSpan.FromHours(1), gatewayUse: false);

        // Act
        bool verified = ControlPlaneTokenVerifier.TryVerify(token, [_issuer], _now, out ControlPlanePrincipal principal);

        // Assert
        verified.ShouldBeTrue();
        principal.CanDispatchCommands.ShouldBeFalse();
    }

    [Fact(DisplayName = displayPrefix + "A lifetime exactly at the eight-hour ceiling is accepted")]
    public void TryVerify_WhenLifetimeEqualsCeiling_ReturnsTrue()
    {
        // Arrange
        string token = Issue(TimeSpan.FromHours(8), gatewayUse: true);

        // Act
        bool verified = ControlPlaneTokenVerifier.TryVerify(token, [_issuer], _now, out _);

        // Assert
        verified.ShouldBeTrue();
    }

    [Theory(DisplayName = displayPrefix + "Credentials outside the profile are refused")]
    [InlineData("over-ceiling")]
    [InlineData("wrong-audience")]
    [InlineData("untrusted-issuer")]
    [InlineData("unknown-kid")]
    public void TryVerify_WhenCredentialViolatesProfile_ReturnsFalse(string violation)
    {
        // Arrange
        string token = violation switch
        {
            "over-ceiling" => Issue(TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1), gatewayUse: true),
            "wrong-audience" => Issue(TimeSpan.FromHours(1), gatewayUse: true, audience: "api"),
            "untrusted-issuer" => Issue(TimeSpan.FromHours(1), gatewayUse: true, issuer: "appc"),
            _ => Issue(TimeSpan.FromHours(1), gatewayUse: true, keyId: "unknown"),
        };

        // Act
        bool verified = ControlPlaneTokenVerifier.TryVerify(token, [_issuer], _now, out ControlPlanePrincipal principal);

        // Assert
        verified.ShouldBeFalse();
        principal.ShouldBe(default);
    }

    private string Issue(
        TimeSpan lifetime,
        bool gatewayUse,
        string audience = "cohesion-export",
        string issuer = issuerName,
        string? keyId = null)
    {
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = issuer,
            Subject = new SubjectIdentifier("gateway", issuer: issuer),
            IssuedAt = _now,
            NotBefore = _now,
            ExpiresAt = _now.Add(lifetime),
        };
        descriptor.Audiences.Add(audience);
        if (gatewayUse)
        {
            descriptor.Claims.Add(new IdentityClaim("cohesion_token_use", "gateway"));
        }

        return JsonWebTokenWriter.CreateEs256(_signingKey, keyId ?? _keyId).Write(descriptor);
    }
}

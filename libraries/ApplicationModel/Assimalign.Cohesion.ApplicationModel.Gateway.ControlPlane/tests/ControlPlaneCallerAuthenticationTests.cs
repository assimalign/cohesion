using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

/// <summary>
/// The control plane's caller pipeline: the built-in ES256 trusted-issuer authenticator first, then
/// each registered <see cref="IApplicationCallerAuthenticator"/> in order, where <c>NoResult</c> falls
/// through and the first final status wins.
/// </summary>
public sealed class ControlPlaneCallerAuthenticationTests : IDisposable
{
    private const string displayPrefix = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Caller authentication: ";
    private static readonly DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApplicationName _application = "appa";

    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;
    private readonly TrustedIssuer _peer;
    private readonly ConcurrentQueue<string> _log = new();

    public ControlPlaneCallerAuthenticationTests()
    {
        ECParameters parameters = _signingKey.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X!);
        string y = Base64Url.EncodeToString(parameters.Q.Y!);
        _keyId = Base64Url.EncodeToString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}")));
        using JsonDocument publicKey = JsonDocument.Parse(
            $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{x}\",\"y\":\"{y}\",\"kid\":\"{_keyId}\",\"alg\":\"ES256\",\"use\":\"sig\"}}");
        _peer = new TrustedIssuer("peer", publicKey.RootElement, ["test.command"]);
    }

    public void Dispose() => _signingKey.Dispose();

    [Fact(DisplayName = displayPrefix + "A verified application-key token is admitted before any registered authenticator runs")]
    public async Task AuthenticateAsync_VerifiedApplicationKeyToken_ShouldNotConsultRegisteredAuthenticators()
    {
        // Arrange
        RecordingCallerAuthenticator first = Authenticator("first", _ => Refuse(ApplicationCallerStatus.Forbidden));

        // Act
        ApplicationCallerResult result = await AuthenticateAsync("Bearer", IssuePeerToken(gatewayUse: true), first);

        // Assert
        result.Status.ShouldBe(ApplicationCallerStatus.Authenticated);
        ApplicationCaller caller = result.Caller.ShouldNotBeNull();
        caller.Application.ShouldBe((ApplicationName)"peer");
        caller.Subject.ShouldBe("gateway");
        caller.Kind.ShouldBe(ApplicationCallerKind.Peer);
        caller.AllowedCommandKinds.ShouldBe(["test.command"]);
        _log.ShouldBeEmpty();
    }

    [Fact(DisplayName = displayPrefix + "A trusted developer token maps to a read-only developer caller")]
    public async Task AuthenticateAsync_TrustedDeveloperToken_ShouldMapToDeveloperCaller()
    {
        // Act
        ApplicationCallerResult result = await AuthenticateAsync("bearer", IssuePeerToken(gatewayUse: false));

        // Assert
        ApplicationCaller caller = result.Caller.ShouldNotBeNull();
        caller.Kind.ShouldBe(ApplicationCallerKind.Developer);
        ControlPlaneCallerAuthentication.CanDispatchCommands(caller).ShouldBeFalse();
    }

    [Fact(DisplayName = displayPrefix + "NoResult falls through in registration order and the first authenticated result wins")]
    public async Task AuthenticateAsync_NoResultThenAuthenticated_ShouldStopAtTheFirstFinalResult()
    {
        // Arrange
        var peer = new ApplicationCaller("caller", "idp-subject", ApplicationCallerKind.Peer, ["test.command"]);
        RecordingCallerAuthenticator first = Authenticator("first", _ => default);
        RecordingCallerAuthenticator second = Authenticator("second", _ => new ApplicationCallerResult(ApplicationCallerStatus.Authenticated, peer, null));
        RecordingCallerAuthenticator third = Authenticator("third", _ => Refuse(ApplicationCallerStatus.Forbidden));

        // Act
        ApplicationCallerResult result = await AuthenticateAsync("Bearer", "idp-token", first, second, third);

        // Assert
        result.Status.ShouldBe(ApplicationCallerStatus.Authenticated);
        result.Caller.ShouldBeSameAs(peer);
        _log.ShouldBe(["first", "second"]);
        ApplicationCallerRequest request = second.Requests.ShouldHaveSingleItem();
        request.Application.ShouldBe(_application);
        request.Scheme.ShouldBe("Bearer");
        request.Credential.ShouldBe("idp-token");
        ControlPlaneCallerAuthentication.CanDispatchCommands(peer).ShouldBeTrue();
    }

    [Theory(DisplayName = displayPrefix + "An Unauthorized or Forbidden result is final")]
    [InlineData(ApplicationCallerStatus.Unauthorized)]
    [InlineData(ApplicationCallerStatus.Forbidden)]
    public async Task AuthenticateAsync_FinalRefusal_ShouldStopThePipeline(ApplicationCallerStatus status)
    {
        // Arrange
        RecordingCallerAuthenticator first = Authenticator("first", _ => Refuse(status));
        RecordingCallerAuthenticator second = Authenticator("second", _ => default);

        // Act
        ApplicationCallerResult result = await AuthenticateAsync("DPoP", "idp-token", first, second);

        // Assert
        result.Status.ShouldBe(status);
        result.Failure.ShouldBe("refused by the identity provider");
        _log.ShouldBe(["first"]);
        first.Requests.ShouldHaveSingleItem().Scheme.ShouldBe("DPoP");
    }

    [Fact(DisplayName = displayPrefix + "A credential the built-in authenticator cannot verify reaches every authenticator, and all passing is NoResult")]
    public async Task AuthenticateAsync_UnverifiedTokenAndEveryAuthenticatorPasses_ShouldReturnNoResult()
    {
        // Arrange: a well-formed ES256 token from an issuer the application does not trust.
        string untrusted = IssuePeerToken(gatewayUse: true, issuer: "stranger");
        RecordingCallerAuthenticator first = Authenticator("first", _ => default);
        RecordingCallerAuthenticator second = Authenticator("second", _ => default);

        // Act
        ApplicationCallerResult result = await AuthenticateAsync("Bearer", untrusted, first, second);
        ApplicationCallerResult unregistered = await AuthenticateAsync("Bearer", untrusted);

        // Assert
        result.Status.ShouldBe(ApplicationCallerStatus.NoResult);
        result.Caller.ShouldBeNull();
        unregistered.Status.ShouldBe(ApplicationCallerStatus.NoResult);
        _log.ShouldBe(["first", "second"]);
        second.Requests.ShouldHaveSingleItem().Credential.ShouldBe(untrusted);
    }

    [Fact(DisplayName = displayPrefix + "An Authenticated result without a caller is a defect, never an admission")]
    public async Task AuthenticateAsync_AuthenticatedWithoutCaller_ShouldThrow()
    {
        // Arrange: a 'with' expression on a default result bypasses the record's constructor check.
        RecordingCallerAuthenticator broken = Authenticator(
            "broken",
            _ => default(ApplicationCallerResult) with { Status = ApplicationCallerStatus.Authenticated });
        RecordingCallerAuthenticator next = Authenticator("next", _ => default);

        // Act
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => AuthenticateAsync("Bearer", "idp-token", broken, next).AsTask());

        // Assert
        failure.Message.ShouldContain("without an ApplicationCaller", Case.Sensitive);
        _log.ShouldBe(["broken"]);
    }

    private ValueTask<ApplicationCallerResult> AuthenticateAsync(
        string scheme,
        string credential,
        params IApplicationCallerAuthenticator[] authenticators) =>
        ControlPlaneCallerAuthentication.AuthenticateAsync(
            _application,
            scheme,
            credential,
            [_peer],
            authenticators,
            _now,
            CancellationToken.None);

    private RecordingCallerAuthenticator Authenticator(
        string name,
        Func<ApplicationCallerRequest, ApplicationCallerResult> authenticate) =>
        new(name, _log, authenticate);

    private static ApplicationCallerResult Refuse(ApplicationCallerStatus status) =>
        new(status, null, "refused by the identity provider");

    private string IssuePeerToken(bool gatewayUse, string issuer = "peer")
    {
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = issuer,
            Subject = new SubjectIdentifier("gateway", issuer: issuer),
            IssuedAt = _now,
            NotBefore = _now,
            ExpiresAt = _now.AddHours(1),
        };
        descriptor.Audiences.Add("cohesion-export");
        if (gatewayUse)
        {
            descriptor.Claims.Add(new IdentityClaim("cohesion_token_use", "gateway"));
        }

        return JsonWebTokenWriter.CreateEs256(_signingKey, _keyId).Write(descriptor);
    }
}

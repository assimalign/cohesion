using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Bearer.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Bearer.Tests;

public class JwtBearerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] SecretKey = Encoding.UTF8.GetBytes("this-is-a-256-bit-hmac-test-key-value!!");

    private const string Issuer = "https://issuer.example";
    private const string Audience = "api://default";

    private static async Task<IAuthenticationHandler> InitializeAsync(JwtBearerOptions options, TestHttpContext context)
    {
        IAuthenticationHandler handler = JwtBearerAuthentication.CreateHandler(options);
        AuthenticationScheme scheme = new(JwtBearerDefaults.AuthenticationScheme, null, () => handler);
        await handler.InitializeAsync(scheme, context);
        return handler;
    }

    private static JwtBearerOptions HmacOptions()
    {
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(Now) };
        options.SigningKeys.Add(JwtSignatureVerifier.CreateHmac(SecretKey));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);
        return options;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A valid HS256 token authenticates the principal")]
    public async Task Authenticate_ValidHmacToken_Succeeds()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        string token = TestJwt.Hmac(SecretKey, TestJwt.Payload(Now, Issuer, Audience, name: "alice", roles: new[] { "admin", "user" }));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal!.Identity!.IsAuthenticated.ShouldBeTrue();
        result.Principal.Identity.Name.ShouldBe("alice");
        result.Principal.IsInRole("admin").ShouldBeTrue();
        result.Principal.IsInRole("user").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A missing Authorization header yields NoResult")]
    public async Task Authenticate_NoHeader_NoResult()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A non-Bearer Authorization header yields NoResult")]
    public async Task Authenticate_NonBearerHeader_NoResult()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Basic dXNlcjpwYXNz");
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A malformed token fails")]
    public async Task Authenticate_MalformedToken_Fails()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer not-a-jwt");
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A token signed with the wrong key fails")]
    public async Task Authenticate_WrongSignature_Fails()
    {
        // Arrange
        byte[] attackerKey = Encoding.UTF8.GetBytes("a-completely-different-256-bit-key-value!");
        string token = TestJwt.Hmac(attackerKey, TestJwt.Payload(Now, Issuer, Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - An expired token fails")]
    public async Task Authenticate_ExpiredToken_Fails()
    {
        // Arrange — token issued and expired an hour before 'now'.
        string token = TestJwt.Hmac(SecretKey, TestJwt.Payload(Now - TimeSpan.FromHours(2), Issuer, Audience, lifetime: TimeSpan.FromHours(1)));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A token from an untrusted issuer fails")]
    public async Task Authenticate_WrongIssuer_Fails()
    {
        // Arrange
        string token = TestJwt.Hmac(SecretKey, TestJwt.Payload(Now, issuer: "https://evil.example", audience: Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A token for the wrong audience fails")]
    public async Task Authenticate_WrongAudience_Fails()
    {
        // Arrange
        string token = TestJwt.Hmac(SecretKey, TestJwt.Payload(Now, Issuer, audience: "api://other"));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - The unsecured 'none' algorithm is rejected")]
    public async Task Authenticate_NoneAlgorithm_Fails()
    {
        // Arrange
        string token = TestJwt.Unsecured(TestJwt.Payload(Now, Issuer, Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - An HS256 token is rejected when only an RSA key is configured")]
    public async Task Authenticate_AlgorithmMismatch_Fails()
    {
        // Arrange — the algorithm-confusion guard: no configured verifier accepts HS256.
        using RSA rsa = RSA.Create(2048);
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(Now) };
        options.SigningKeys.Add(JwtSignatureVerifier.CreateRsa(rsa));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);

        string token = TestJwt.Hmac(SecretKey, TestJwt.Payload(Now, Issuer, Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A valid RS256 token authenticates")]
    public async Task Authenticate_ValidRsaToken_Succeeds()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(Now) };
        options.SigningKeys.Add(JwtSignatureVerifier.CreateRsa(rsa));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);

        string token = TestJwt.Rsa(rsa, TestJwt.Payload(Now, Issuer, Audience, name: "bob"));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal!.Identity!.Name.ShouldBe("bob");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A valid ES256 token authenticates")]
    public async Task Authenticate_ValidEcdsaToken_Succeeds()
    {
        // Arrange
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(Now) };
        options.SigningKeys.Add(JwtSignatureVerifier.CreateEcdsa(ecdsa));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);

        string token = TestJwt.Ecdsa(ecdsa, TestJwt.Payload(Now, Issuer, Audience, name: "carol"));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal!.Identity!.Name.ShouldBe("carol");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Challenge emits a 401 Bearer WWW-Authenticate header")]
    public async Task Challenge_Emits401BearerHeader()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.Realm = "api";
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.ChallengeAsync(properties: null);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(401);
        string? wwwAuthenticate = context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate);
        wwwAuthenticate.ShouldNotBeNull();
        wwwAuthenticate!.ShouldStartWith("Bearer");
        wwwAuthenticate.ShouldContain("realm=\"api\"");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Challenge after a failed authenticate advertises invalid_token")]
    public async Task Challenge_AfterFailure_AdvertisesInvalidToken()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer not-a-jwt");
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.AuthenticateAsync();
        await handler.ChallengeAsync(properties: null);

        // Assert
        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate)!.ShouldContain("error=\"invalid_token\"");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Forbid emits a 403 insufficient_scope challenge")]
    public async Task Forbid_Emits403InsufficientScope()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        await handler.ForbidAsync(properties: null);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(403);
        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate)!.ShouldContain("insufficient_scope");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - CreateHandler requires a signing key when signatures are required")]
    public void CreateHandler_NoSigningKey_Throws()
    {
        // Arrange
        JwtBearerOptions options = new();

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => JwtBearerAuthentication.CreateHandler(options));
    }
}

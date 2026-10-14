using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Bearer.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Bearer.Tests;

public class JwtBearerHandlerTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Create: returns the public concrete handler implementing its contract")]
    public async Task Create_WithOptions_ReturnsConcreteHandler()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        TestHttpContext context = TestHttpContext.Create();

        // Act
        JwtBearerHandler handler = JwtBearerHandler.Create(options);
        IAuthenticationHandler contract = handler;
        await contract.InitializeAsync(new AuthenticationScheme("Bearer", null, () => handler), context, CancellationToken.None);
        AuthenticateResult result = await contract.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.None.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Create: concrete signature verifiers retain their algorithm and key selection")]
    public void Create_WithKeys_ReturnsConcreteSignatureVerifiers()
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] input = Encoding.ASCII.GetBytes("header.payload");

        // Act
        HmacJwtSignatureVerifier hmacVerifier = HmacJwtSignatureVerifier.Create(_secretKey, "hmac");
        RsaJwtSignatureVerifier rsaVerifier = RsaJwtSignatureVerifier.Create(rsa, "rsa");
        EcdsaJwtSignatureVerifier ecdsaVerifier = EcdsaJwtSignatureVerifier.Create(ecdsa, "ec");
        IJwtSignatureVerifier[] contracts = [hmacVerifier, rsaVerifier, ecdsaVerifier];

        // Assert
        contracts[0].CanVerify("HS256", "hmac").ShouldBeTrue();
        contracts[0].Verify("HS256", input, HMACSHA256.HashData(_secretKey, input)).ShouldBeTrue();
        contracts[1].CanVerify("RS256", "rsa").ShouldBeTrue();
        contracts[1].CanVerify("HS256", "rsa").ShouldBeFalse();
        contracts[1].Verify("RS256", input, rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ShouldBeTrue();
        contracts[2].CanVerify("ES256", "ec").ShouldBeTrue();
        contracts[2].CanVerify("ES256", "wrong-key").ShouldBeFalse();
        contracts[2].Verify("ES256", input, ecdsa.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ShouldBeTrue();
    }

    private static readonly DateTimeOffset _now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] _secretKey = Encoding.UTF8.GetBytes("this-is-a-256-bit-hmac-test-key-value!!");

    private const string Issuer = "https://issuer.example";
    private const string Audience = "api://default";

    private static async Task<IAuthenticationHandler> InitializeAsync(JwtBearerOptions options, TestHttpContext context)
    {
        IAuthenticationHandler handler = JwtBearerHandler.Create(options);
        AuthenticationScheme scheme = new(JwtBearerDefaults.AuthenticationScheme, null, () => handler);
        await handler.InitializeAsync(scheme, context);
        return handler;
    }

    private static JwtBearerOptions HmacOptions()
    {
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(_now) };
        options.SigningKeys.Add(HmacJwtSignatureVerifier.Create(_secretKey));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);
        return options;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - A valid HS256 token authenticates the principal")]
    public async Task Authenticate_ValidHmacToken_Succeeds()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, Issuer, Audience, name: "alice", roles: new[] { "admin", "user" }));
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
        string token = TestJwt.Hmac(attackerKey, TestJwt.Payload(_now, Issuer, Audience));
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
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now - TimeSpan.FromHours(2), Issuer, Audience, lifetime: TimeSpan.FromHours(1)));
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
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, issuer: "https://evil.example", audience: Audience));
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
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, Issuer, audience: "api://other"));
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
        string token = TestJwt.Unsecured(TestJwt.Payload(_now, Issuer, Audience));
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
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(_now) };
        options.SigningKeys.Add(RsaJwtSignatureVerifier.Create(rsa));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);

        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, Issuer, Audience));
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
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(_now) };
        options.SigningKeys.Add(RsaJwtSignatureVerifier.Create(rsa));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);

        string token = TestJwt.Rsa(rsa, TestJwt.Payload(_now, Issuer, Audience, name: "bob"));
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
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(_now) };
        options.SigningKeys.Add(EcdsaJwtSignatureVerifier.Create(ecdsa));
        options.ValidIssuers.Add(Issuer);
        options.ValidAudiences.Add(Audience);

        string token = TestJwt.Ecdsa(ecdsa, TestJwt.Payload(_now, Issuer, Audience, name: "carol"));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal!.Identity!.Name.ShouldBe("carol");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Realm: preserves quotes and backslashes with quoted-pair escaping")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Challenge_QuotedRealm_ShouldEscapeWithoutChangingItsValue(bool forbid)
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.Realm = "api\"\\realm";
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        if (forbid)
        {
            await handler.ForbidAsync(null, CancellationToken.None);
        }
        else
        {
            await handler.ChallengeAsync(null, CancellationToken.None);
        }

        // Assert
        string expected = "Bearer realm=\"api\\\"\\\\realm\"";
        if (forbid)
        {
            expected += ", error=\"insufficient_scope\"";
        }

        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Realm: optional null and empty values omit the parameter")]
    [InlineData(null)]
    [InlineData("")]
    public async Task Challenge_AbsentRealm_ShouldOmitParameter(string? realm)
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.Realm = realm;
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.ChallengeAsync(null, CancellationToken.None);

        // Assert
        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate).ShouldBe("Bearer");
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

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Challenges preserve all schemes in either order on 401 and 403")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Challenge_WithOtherSchemesInEitherOrder_PreservesEveryChallenge(bool forbid, bool bearerFirst)
    {
        // Arrange — a prior scheme has challenged; another runs before or after Bearer.
        TestHttpContext context = TestHttpContext.Create();
        context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, "Negotiate");
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);
        string bearer = forbid ? "Bearer error=\"insufficient_scope\"" : "Bearer";

        // Act
        if (!bearerFirst)
        {
            context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, "Custom");
        }

        if (forbid)
        {
            await handler.ForbidAsync(null);
        }
        else
        {
            await handler.ChallengeAsync(null);
        }

        if (bearerFirst)
        {
            context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, "Custom");
        }

        // Assert
        context.Response.StatusCode.Value.ShouldBe(forbid ? 403 : 401);
        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate).ShouldBe(bearerFirst
            ? "Negotiate," + bearer + ",Custom"
            : "Negotiate,Custom," + bearer);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - CreateHandler requires a signing key when signatures are required")]
    public void CreateHandler_NoSigningKey_Throws()
    {
        // Arrange
        JwtBearerOptions options = new();

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => JwtBearerHandler.Create(options));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - CreateHandler fails closed when no issuer is configured")]
    public void CreateHandler_NoValidIssuers_Throws()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.ValidIssuers.Clear();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => JwtBearerHandler.Create(options));

        // Assert
        exception.Message.ShouldContain(nameof(JwtBearerOptions.ValidIssuers), Case.Sensitive);
        exception.Message.ShouldContain(nameof(JwtBearerOptions.ValidateIssuer), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - CreateHandler fails closed when no audience is configured")]
    public void CreateHandler_NoValidAudiences_Throws()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.ValidAudiences.Clear();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => JwtBearerHandler.Create(options));

        // Assert
        exception.Message.ShouldContain(nameof(JwtBearerOptions.ValidAudiences), Case.Sensitive);
        exception.Message.ShouldContain(nameof(JwtBearerOptions.ValidateAudience), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Turning issuer validation off accepts any issuer a key verifies")]
    public async Task Authenticate_IssuerValidationDisabled_AcceptsAnyIssuer()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.ValidIssuers.Clear();
        options.ValidateIssuer = false;
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, issuer: "https://other-issuer.example", audience: Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Turning audience validation off accepts any audience")]
    public async Task Authenticate_AudienceValidationDisabled_AcceptsAnyAudience()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.ValidAudiences.Clear();
        options.ValidateAudience = false;
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, Issuer, audience: "api://another-service"));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Issuer validation still applies with audience validation off")]
    public async Task Authenticate_AudienceValidationDisabled_StillRejectsWrongIssuer()
    {
        // Arrange
        JwtBearerOptions options = HmacOptions();
        options.ValidAudiences.Clear();
        options.ValidateAudience = false;
        string token = TestJwt.Hmac(_secretKey, TestJwt.Payload(_now, issuer: "https://evil.example", audience: Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - An RS256 token is rejected when only an HMAC key is configured")]
    public async Task Authenticate_AsymmetricTokenWithOnlyHmacKey_Fails()
    {
        // Arrange — with AllowedAlgorithms empty the accepted set is bounded by the key types, so an
        // HMAC-only scheme accepts no asymmetric algorithm (RFC 8725 §3.1).
        using RSA rsa = RSA.Create(2048);
        string token = TestJwt.Rsa(rsa, TestJwt.Payload(_now, Issuer, Audience));
        TestHttpContext context = TestHttpContext.Create();
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = await InitializeAsync(HmacOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }
}

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public sealed class IdentityHubProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub.Hosting] - OIDC: discovery, persisted JWKS, client credentials, and device grant round trip")]
    public async Task Oidc_WithRegisteredClients_ShouldRoundTripSignedTokensAndPersistKey()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        Uri endpoint = IdentityHubTestHost.GetEndpoint();
        IdentityHubApplicationBuilder builder = CreateConfiguredBuilder(data.Path, endpoint);
        await using IdentityHubApplication application = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ((IHost)application).StartAsync(timeout.Token);

        string firstKid;
        string firstX;
        string firstY;
        try
        {
            using var client = new HttpClient();

            // Act: discovery and key publication.
            using JsonDocument discovery = await GetJsonAsync(
                client,
                new Uri(endpoint, "/.well-known/openid-configuration"),
                timeout.Token);
            using JsonDocument jwks = await GetJsonAsync(
                client,
                new Uri(endpoint, "/oauth2/jwks"),
                timeout.Token);
            JsonElement jwk = jwks.RootElement.GetProperty("keys")[0];
            firstKid = jwk.GetProperty("kid").GetString()!;
            firstX = jwk.GetProperty("x").GetString()!;
            firstY = jwk.GetProperty("y").GetString()!;

            using HttpResponseMessage unknownClient = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "unknown",
                    ["client_secret"] = "secret",
                    ["audience"] = "api",
                },
                timeout.Token);
            using HttpResponseMessage badSecret = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "service",
                    ["client_secret"] = "wrong",
                    ["audience"] = "api",
                },
                timeout.Token);
            using HttpResponseMessage badAudience = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "service",
                    ["client_secret"] = "correct-horse",
                    ["audience"] = "other",
                },
                timeout.Token);
            using HttpResponseMessage tokenResponse = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "service",
                    ["client_secret"] = "correct-horse",
                    ["audience"] = "api",
                    ["scope"] = "openid",
                },
                timeout.Token);
            using JsonDocument tokenDocument = JsonDocument.Parse(
                await tokenResponse.Content.ReadAsStringAsync(timeout.Token));
            string accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()!;

            using HttpResponseMessage deviceResponse = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/device_authorization"),
                new Dictionary<string, string>
                {
                    ["client_id"] = "device",
                    ["client_secret"] = "device-secret",
                    ["audience"] = "api",
                    ["scope"] = "openid",
                },
                timeout.Token);
            using JsonDocument deviceDocument = JsonDocument.Parse(
                await deviceResponse.Content.ReadAsStringAsync(timeout.Token));
            string deviceCode = deviceDocument.RootElement.GetProperty("device_code").GetString()!;
            string userCode = deviceDocument.RootElement.GetProperty("user_code").GetString()!;
            var devicePoll = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = "device",
                ["device_code"] = deviceCode,
                ["client_secret"] = "device-secret",
            };
            using var substitutedRequest = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(endpoint, "/oauth2/token"))
            {
                Content = new FormUrlEncodedContent(devicePoll),
            };
            substitutedRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("service:correct-horse")));
            using HttpResponseMessage substitutedClient = await client.SendAsync(
                substitutedRequest,
                timeout.Token);
            using HttpResponseMessage pending = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                devicePoll,
                timeout.Token);
            using HttpResponseMessage approval = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/device"),
                new Dictionary<string, string>
                {
                    ["user_code"] = userCode,
                    ["subject"] = "alice",
                },
                timeout.Token);
            using HttpResponseMessage issuedDevice = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                devicePoll,
                timeout.Token);
            using JsonDocument issuedDeviceDocument = JsonDocument.Parse(
                await issuedDevice.Content.ReadAsStringAsync(timeout.Token));

            // Assert.
            discovery.RootElement.GetProperty("issuer").GetString().ShouldBe(endpoint.ToString().TrimEnd('/'));
            discovery.RootElement.GetProperty("jwks_uri").GetString().ShouldBe(new Uri(endpoint, "/oauth2/jwks").ToString());
            discovery.RootElement.GetProperty("device_authorization_endpoint").GetString()
                .ShouldBe(new Uri(endpoint, "/oauth2/device_authorization").ToString());
            jwk.GetProperty("kty").GetString().ShouldBe("EC");
            jwk.GetProperty("crv").GetString().ShouldBe("P-256");
            jwk.GetProperty("alg").GetString().ShouldBe("ES256");
            discovery.RootElement.GetProperty("id_token_signing_alg_values_supported")[0]
                .GetString().ShouldBe("ES256");
            discovery.RootElement.GetProperty("scopes_supported")[0].GetString().ShouldBe("openid");
            discovery.RootElement.GetProperty("token_endpoint_auth_methods_supported")
                .EnumerateArray().Select(value => value.GetString()).ShouldContain("none");
            unknownClient.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            badSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            badAudience.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            tokenResponse.Headers.CacheControl!.NoStore.ShouldBeTrue();
            tokenResponse.Headers.Pragma.ToString().ShouldBe("no-cache");
            deviceResponse.Headers.CacheControl!.NoStore.ShouldBeTrue();
            deviceResponse.Headers.Pragma.ToString().ShouldBe("no-cache");
            ValidateToken(accessToken, jwk, endpoint.ToString().TrimEnd('/'), "api", "service");
            substitutedClient.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await substitutedClient.Content.ReadAsStringAsync(timeout.Token))
                .ShouldContain("invalid_request");
            pending.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await pending.Content.ReadAsStringAsync(timeout.Token)).ShouldContain("authorization_pending");
            approval.StatusCode.ShouldBe(HttpStatusCode.OK);
            issuedDevice.StatusCode.ShouldBe(HttpStatusCode.OK);
            string deviceAccessToken = issuedDeviceDocument.RootElement.GetProperty("access_token").GetString()!;
            string identityToken = issuedDeviceDocument.RootElement.GetProperty("id_token").GetString()!;
            ValidateToken(
                deviceAccessToken,
                jwk,
                endpoint.ToString().TrimEnd('/'),
                "api",
                "development-user");
            ValidateToken(
                identityToken,
                jwk,
                endpoint.ToString().TrimEnd('/'),
                "device",
                "development-user");
        }
        finally
        {
            await ((IHost)application).StopAsync(timeout.Token);
        }

        // Act: restart from the same data mount on another listener.
        Uri restartedEndpoint = IdentityHubTestHost.GetEndpoint();
        await using IdentityHubApplication restarted = CreateConfiguredBuilder(data.Path, restartedEndpoint).Build();
        await ((IHost)restarted).StartAsync(timeout.Token);
        try
        {
            using var client = new HttpClient();
            using JsonDocument restartedJwks = await GetJsonAsync(
                client,
                new Uri(restartedEndpoint, "/oauth2/jwks"),
                timeout.Token);
            JsonElement restartedKey = restartedJwks.RootElement.GetProperty("keys")[0];

            // Assert: all public coordinates, including kid, came from the persisted key.
            restartedKey.GetProperty("kid").GetString().ShouldBe(firstKid);
            restartedKey.GetProperty("x").GetString().ShouldBe(firstX);
            restartedKey.GetProperty("y").GetString().ShouldBe(firstY);
        }
        finally
        {
            await ((IHost)restarted).StopAsync(timeout.Token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Protocol validation: unsupported scopes and mixed client credentials are rejected")]
    public async Task ProtocolValidation_WithUnsupportedScopesOrMixedCredentials_ShouldRejectRequests()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        Uri endpoint = IdentityHubTestHost.GetEndpoint();
        await using IdentityHubApplication application = CreateConfiguredBuilder(data.Path, endpoint).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ((IHost)application).StartAsync(timeout.Token);

        try
        {
            using var client = new HttpClient();
            var clientCredentials = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "service",
                ["client_secret"] = "correct-horse",
                ["audience"] = "api",
            };
            var deviceAuthorization = new Dictionary<string, string>
            {
                ["client_id"] = "device",
                ["client_secret"] = "device-secret",
                ["audience"] = "api",
            };

            // Act
            var unsupportedClientScope = new Dictionary<string, string>(clientCredentials)
            {
                ["scope"] = "read",
            };
            using HttpResponseMessage invalidClientScope = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                unsupportedClientScope,
                timeout.Token);

            var emptyClientScope = new Dictionary<string, string>(clientCredentials)
            {
                ["scope"] = string.Empty,
            };
            using HttpResponseMessage unscopedToken = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/token"),
                emptyClientScope,
                timeout.Token);
            using JsonDocument unscopedTokenDocument = JsonDocument.Parse(
                await unscopedToken.Content.ReadAsStringAsync(timeout.Token));

            var unsupportedDeviceScope = new Dictionary<string, string>(deviceAuthorization)
            {
                ["scope"] = "openid profile",
            };
            using HttpResponseMessage invalidDeviceScope = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/device_authorization"),
                unsupportedDeviceScope,
                timeout.Token);

            using HttpResponseMessage unauthenticatedDevice = await PostFormAsync(
                client,
                new Uri(endpoint, "/oauth2/device_authorization"),
                new Dictionary<string, string>
                {
                    ["client_id"] = "device",
                    ["audience"] = "api",
                    ["scope"] = "openid",
                },
                timeout.Token);

            using var mixedTokenRequest = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(endpoint, "/oauth2/token"))
            {
                Content = new FormUrlEncodedContent(clientCredentials),
            };
            mixedTokenRequest.Headers.Authorization = CreateBasicAuthorization(
                "service",
                "correct-horse");
            using HttpResponseMessage mixedTokenCredentials = await client.SendAsync(
                mixedTokenRequest,
                timeout.Token);

            using var mixedDeviceRequest = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(endpoint, "/oauth2/device_authorization"))
            {
                Content = new FormUrlEncodedContent(deviceAuthorization),
            };
            mixedDeviceRequest.Headers.Authorization = CreateBasicAuthorization(
                "device",
                "device-secret");
            using HttpResponseMessage mixedDeviceCredentials = await client.SendAsync(
                mixedDeviceRequest,
                timeout.Token);

            // Assert
            invalidClientScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await invalidClientScope.Content.ReadAsStringAsync(timeout.Token))
                .ShouldContain("invalid_scope");
            unscopedToken.StatusCode.ShouldBe(HttpStatusCode.OK);
            unscopedTokenDocument.RootElement.TryGetProperty("scope", out _).ShouldBeFalse();
            invalidDeviceScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await invalidDeviceScope.Content.ReadAsStringAsync(timeout.Token))
                .ShouldContain("invalid_scope");
            unauthenticatedDevice.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            unauthenticatedDevice.Headers.WwwAuthenticate.ToString().ShouldBe("Basic");
            mixedTokenCredentials.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await mixedTokenCredentials.Content.ReadAsStringAsync(timeout.Token))
                .ShouldContain("invalid_request");
            mixedDeviceCredentials.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await mixedDeviceCredentials.Content.ReadAsStringAsync(timeout.Token))
                .ShouldContain("invalid_request");
        }
        finally
        {
            await ((IHost)application).StopAsync(timeout.Token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Control plane: bootstrap JWT protects every namespaced route")]
    public async Task ControlPlane_WithBootstrapJwt_ShouldEnforceMissingWrongAudienceAndValidCredentials()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using var identity = new TestBootstrapIdentity();
        Uri endpoint = IdentityHubTestHost.GetEndpoint();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(
            data.Path,
            endpoint,
            gatewayName: identity.Subject,
            applicationTrustKey: identity.PublicKey);
        await using IdentityHubApplication application = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ((IHost)application).StartAsync(timeout.Token);

        try
        {
            using var client = new HttpClient();
            Uri managedRoute = new(endpoint, "/cohesion/v1/healthz");

            // Act.
            using HttpResponseMessage publicDiscovery = await client.GetAsync(
                new Uri(endpoint, "/.well-known/openid-configuration"),
                timeout.Token);
            using HttpResponseMessage missing = await client.GetAsync(managedRoute, timeout.Token);
            using HttpResponseMessage wrongAudience = await SendBearerAsync(
                client,
                managedRoute,
                identity.Issue("another-resource"),
                timeout.Token);
            using HttpResponseMessage valid = await SendBearerAsync(
                client,
                managedRoute,
                identity.Issue("identity"),
                timeout.Token);

            // Assert.
            publicDiscovery.StatusCode.ShouldBe(HttpStatusCode.OK);
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            missing.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
            wrongAudience.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            valid.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await ((IHost)application).StopAsync(timeout.Token);
        }
    }

    private static IdentityHubApplicationBuilder CreateConfiguredBuilder(string dataPath, Uri endpoint)
    {
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(dataPath, endpoint);
        builder.AddAudience("api");
        builder.AddClient("service", options =>
        {
            options.ClientSecret = "correct-horse";
            options.Audiences.Add("api");
        });
        builder.AddClient("device", options =>
        {
            options.ClientSecret = "device-secret";
            options.AllowDeviceAuthorization = true;
            options.Audiences.Add("api");
        });
        return builder;
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        Uri uri,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
        => client.PostAsync(uri, new FormUrlEncodedContent(values), cancellationToken);

    private static Task<HttpResponseMessage> SendBearerAsync(
        HttpClient client,
        Uri uri,
        string token,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, cancellationToken);
    }

    private static AuthenticationHeaderValue CreateBasicAuthorization(string clientId, string secret)
        => new(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));

    private static void ValidateToken(
        string compact,
        JsonElement jwk,
        string issuer,
        string audience,
        string subject)
    {
        JsonWebToken token = JsonWebToken.Parse(compact);
        token.Subject!.Value.ShouldBe(subject);
        var options = new JsonWebTokenValidationOptions(DateTimeOffset.UtcNow)
        {
            ExpectedIssuer = issuer,
            ExpectedAudience = audience,
        };
        options.AllowedAlgorithms.Add("ES256");
        foreach (string claim in new[] { "iss", "sub", "aud", "iat", "nbf", "exp", "jti" })
        {
            options.RequiredClaims.Add(claim);
        }

        token.Validate(options).Succeeded.ShouldBeTrue();
        byte[] x = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!);
        byte[] y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!);
        byte[] signature = Base64Url.DecodeFromChars(token.Parts!.Signature);
        byte[] signingInput = Encoding.ASCII.GetBytes(token.SigningInput!);
        try
        {
            using ECDsa key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            });
            IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(
                key,
                jwk.GetProperty("kid").GetString()!);
            verifier.Verify("ES256", signingInput, signature).ShouldBeTrue();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(y);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(signingInput);
        }
    }
}

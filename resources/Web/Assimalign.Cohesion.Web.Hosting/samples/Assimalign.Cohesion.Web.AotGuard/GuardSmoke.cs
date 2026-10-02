using System;
using System.Buffers.Text;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.AotGuard;

/// <summary>
/// The guard's smoke checks: one HTTP exchange per composed feature, each asserting the behavior
/// that proves the feature survived NativeAOT publish and trimming.
/// </summary>
internal static class GuardSmoke
{
    public const string Issuer = "https://aot-guard.cohesion.local";
    public const string Audience = "aot-guard";
    public const string TrustedOrigin = "https://aot-guard-client.cohesion.local";

    public static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task<int> RunAsync(int port, byte[] signingKey, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        int failures = 0;

        failures += await CheckAsync("static files serve wwwroot", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("/", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body.Contains("cohesion-web-aot-guard", StringComparison.Ordinal);
        });

        failures += await CheckAsync("typed route binding writes source-generated JSON", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("items/7", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "application/json"
                && body.Contains("item-7", StringComparison.Ordinal);
        });

        failures += await CheckAsync("typed JSON body binding round-trips", async () =>
        {
            using var content = new StringContent("{\"id\":3,\"name\":\"posted\"}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("items", content, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body.Contains("posted-echo", StringComparison.Ordinal);
        });

        failures += await CheckAsync("validation rejects an invalid body with problem details", async () =>
        {
            using var content = new StringContent("{\"id\":4,\"name\":\"\"}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("items", content, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.BadRequest
                && response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                && body.Contains("\"Name\"", StringComparison.Ordinal);
        });

        failures += await CheckAsync("an uploaded file binds to a typed handler", async () =>
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent("hello upload"u8.ToArray()), "file", "guard.txt");
            using HttpResponseMessage response = await client.PostAsync("upload", form, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body == "guard.txt:12";
        });

        failures += await CheckAsync("a returned model is written as negotiated JSON", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("values/5", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "application/json"
                && body.Contains("value-5", StringComparison.Ordinal);
        });

        failures += await CheckAsync("an awaited return value is written", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("values/async/6", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body.Contains("async-value-6", StringComparison.Ordinal);
        });

        failures += await CheckAsync("a returned string is written as text/plain", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("greeting", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "text/plain"
                && body == "hello from the AOT guard";
        });

        failures += await CheckAsync("a null return answers 204", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("values/none", cancellationToken);
            return response.StatusCode == HttpStatusCode.NoContent;
        });

        failures += await CheckAsync("the OpenAPI document describes the typed endpoints", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("openapi/v1.json", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "application/json"
                && body.Contains("\"/items/{id}\"", StringComparison.Ordinal)
                && body.Contains("\"GuardItem\"", StringComparison.Ordinal)
                && response.Headers.ETag is not null;
        });

        failures += await CheckAsync("the OpenAPI document answers a matching ETag with 304", async () =>
        {
            using HttpResponseMessage first = await client.GetAsync("openapi/v1.json", cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, "openapi/v1.json");
            if (first.Headers.ETag is { } entityTag)
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            using HttpResponseMessage second = await client.SendAsync(request, cancellationToken);
            return first.Headers.ETag is not null && second.StatusCode == HttpStatusCode.NotModified;
        });

        failures += await CheckAsync("CORS answers a JSON preflight from its candidate endpoint's policy", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "items");
            request.Headers.TryAddWithoutValidation("Origin", TrustedOrigin);
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.NoContent
                && Header(response, "Access-Control-Allow-Origin") == TrustedOrigin
                && Header(response, "Access-Control-Allow-Headers") == "Content-Type";
        });

        failures += await CheckAsync("CORS stamps an allowed origin and withholds a denied one", async () =>
        {
            using var allowed = new HttpRequestMessage(HttpMethod.Get, "items/7");
            allowed.Headers.TryAddWithoutValidation("Origin", TrustedOrigin);
            using var denied = new HttpRequestMessage(HttpMethod.Get, "items/7");
            denied.Headers.TryAddWithoutValidation("Origin", "https://untrusted.cohesion.local");
            using HttpResponseMessage allowedResponse = await client.SendAsync(allowed, cancellationToken);
            using HttpResponseMessage deniedResponse = await client.SendAsync(denied, cancellationToken);
            return allowedResponse.StatusCode == HttpStatusCode.OK
                && Header(allowedResponse, "Access-Control-Allow-Origin") == TrustedOrigin
                && deniedResponse.StatusCode == HttpStatusCode.OK
                && Header(deniedResponse, "Access-Control-Allow-Origin") is null;
        });

        failures += await CheckAsync("response compression negotiates gzip", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "large");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentEncoding.Contains("gzip");
        });

        failures += await CheckAsync("JWT bearer rejects an anonymous request", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("me", cancellationToken);
            return response.StatusCode == HttpStatusCode.Unauthorized;
        });

        failures += await CheckAsync("JWT bearer authenticates a signed token", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body == "aot-guard-user";
        });

        failures += await CheckAsync("authorization challenges an anonymous request", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("admin", cancellationToken);
            return response.StatusCode == HttpStatusCode.Unauthorized;
        });

        failures += await CheckAsync("authorization forbids a token without the required role", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "admin");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.Forbidden;
        });

        failures += await CheckAsync("authorization admits a token in the required role", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "admin");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey, role: "admin"));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body == "aot-guard-user";
        });

        failures += await CheckAsync("error handling answers a fault with problem details", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("boom", cancellationToken);
            return response.StatusCode == HttpStatusCode.InternalServerError
                && response.Content.Headers.ContentType?.MediaType == "application/problem+json";
        });

        failures += await CheckAsync("route group binds its prefix value under its endpoint policies", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("tenants/acme/orders/9", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body.Contains("acme-order-9", StringComparison.Ordinal);
        });

        failures += await CheckAsync("path branch sees its base and the remaining path", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("branch/x/y", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body == "/branch|/x/y";
        });

        failures += await CheckAsync("fallback answers a client route with index.html", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("app/settings", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.StatusCode == HttpStatusCode.OK && body.Contains("cohesion-web-aot-guard", StringComparison.Ordinal);
        });

        failures += await CheckAsync("fallback leaves a missing asset 404", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("missing.js", cancellationToken);
            return response.StatusCode == HttpStatusCode.NotFound;
        });

        failures += await CheckAsync("security headers apply their safe defaults", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("items/7", cancellationToken);
            return Header(response, "X-Content-Type-Options") == "nosniff"
                && Header(response, "X-Frame-Options") == "DENY"
                && Header(response, "Referrer-Policy") == "strict-origin-when-cross-origin";
        });

        failures += await CheckAsync("cookie policy upgrades a SameSite=None cookie to Secure", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("cookies", cancellationToken);
            return response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && string.Join("; ", cookies).Contains("guard-tracking=1", StringComparison.Ordinal)
                && string.Join("; ", cookies).Contains("secure", StringComparison.OrdinalIgnoreCase);
        });

        failures += await CheckAsync("antiforgery rejects a protected post without a token", async () =>
        {
            using HttpResponseMessage response = await client.PostAsync("antiforgery/submit", content: null, cancellationToken);
            return response.StatusCode == HttpStatusCode.BadRequest
                && response.Content.Headers.ContentType?.MediaType == "application/problem+json";
        });

        failures += await CheckAsync("antiforgery accepts the cookie and header token pair", async () =>
        {
            // The handler keeps the cookie token the render path sets and sends it back.
            string requestToken = await client.GetStringAsync("antiforgery/token", cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "antiforgery/submit");
            request.Headers.Add("X-CSRF-TOKEN", requestToken);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                && await response.Content.ReadAsStringAsync(cancellationToken) == "accepted";
        });

        Console.WriteLine(failures == 0 ? "AOT guard smoke: all checks passed." : $"AOT guard smoke: {failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> CheckAsync(string name, Func<Task<bool>> check)
    {
        try
        {
            bool passed = await check();
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
            return passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"FAIL: {name} ({exception.GetType().Name}: {exception.Message})");
            return 1;
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static string CreateToken(byte[] signingKey, string? role = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string roles = role is null ? string.Empty : $",\"roles\":[\"{role}\"]";
        string header = Base64Url.EncodeToString("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);
        string payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{{\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\",\"sub\":\"guard\",\"name\":\"aot-guard-user\"{roles},\"iat\":{now},\"nbf\":{now},\"exp\":{now + 300}}}"));
        byte[] signature = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(header + "." + payload));
        return header + "." + payload + "." + Base64Url.EncodeToString(signature);
    }
}

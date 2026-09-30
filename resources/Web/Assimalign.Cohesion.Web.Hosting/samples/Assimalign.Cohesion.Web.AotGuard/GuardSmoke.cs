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

        failures += await CheckAsync("error handling answers a fault with problem details", async () =>
        {
            using HttpResponseMessage response = await client.GetAsync("boom", cancellationToken);
            return response.StatusCode == HttpStatusCode.InternalServerError
                && response.Content.Headers.ContentType?.MediaType == "application/problem+json";
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

    private static string CreateToken(byte[] signingKey)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string header = Base64Url.EncodeToString("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);
        string payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{{\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\",\"sub\":\"guard\",\"name\":\"aot-guard-user\",\"iat\":{now},\"nbf\":{now},\"exp\":{now + 300}}}"));
        byte[] signature = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(header + "." + payload));
        return header + "." + payload + "." + Base64Url.EncodeToString(signature);
    }
}

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication.Bearer.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
{
    private HttpContext? _httpContext;

    public override HttpHost Host { get; set; } = HttpHost.Empty;
    public override HttpPath Path { get; set; } = HttpPath.Root;
    public override HttpMethod Method { get; set; } = HttpMethod.Get;
    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;
    public override HttpQueryCollection Query { get; } = new HttpQueryCollection();
    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public override Stream Body { get; set; } = Stream.Null;

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException("The HttpContext back-reference has not been attached.");

    internal void AttachContext(HttpContext context) => _httpContext ??= context;
}

internal sealed class TestHttpResponse : HttpResponse
{
    private HttpContext? _httpContext;

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException("The HttpContext back-reference has not been attached.");

    internal void AttachContext(HttpContext context) => _httpContext ??= context;
}

internal sealed class TestHttpContext : HttpContext
{
    private TestHttpContext(TestHttpRequest request, TestHttpResponse response)
    {
        Version = HttpVersion.Http11;
        Request = request;
        Response = response;
        ConnectionInfo = HttpConnectionInfo.Empty;
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
        RequestCancelled = CancellationToken.None;

        request.AttachContext(this);
        response.AttachContext(this);
    }

    public override HttpVersion Version { get; }
    public override TestHttpRequest Request { get; }
    public override TestHttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo { get; }
    public override HttpFeatureCollection Features { get; }
    public override IDictionary<string, object?> Items { get; }
    public override CancellationToken RequestCancelled { get; }

    public override void Cancel() { }
    public override Task CancelAsync() => Task.CompletedTask;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TestHttpContext Create() => new(new TestHttpRequest(), new TestHttpResponse());

    public void SetAuthorization(string value)
        => Request.Headers[HttpHeaderKey.Authorization] = value;
}

/// <summary>
/// Mints compact-serialized JWS tokens for the bearer tests. The header/payload JSON is
/// hand-authored so tests control the exact wire bytes the handler validates.
/// </summary>
internal static class TestJwt
{
    public static string Hmac(byte[] key, string payloadJson, string algorithm = "HS256", string? keyId = null)
    {
        string header = BuildHeader(algorithm, keyId);
        string signingInput = Segment(header) + "." + Segment(payloadJson);
        byte[] signature = algorithm switch
        {
            "HS256" => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput)),
            "HS384" => HMACSHA384.HashData(key, Encoding.ASCII.GetBytes(signingInput)),
            "HS512" => HMACSHA512.HashData(key, Encoding.ASCII.GetBytes(signingInput)),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        return signingInput + "." + Encode(signature);
    }

    public static string Rsa(RSA key, string payloadJson, string? keyId = null)
    {
        string header = BuildHeader("RS256", keyId);
        string signingInput = Segment(header) + "." + Segment(payloadJson);
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Encode(signature);
    }

    public static string Ecdsa(ECDsa key, string payloadJson, string? keyId = null)
    {
        string header = BuildHeader("ES256", keyId);
        string signingInput = Segment(header) + "." + Segment(payloadJson);
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Encode(signature);
    }

    public static string Unsecured(string payloadJson)
        => Segment("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Segment(payloadJson) + ".";

    public static string Payload(
        DateTimeOffset now,
        string? issuer = "https://issuer.example",
        string? audience = "api://default",
        string subject = "user-123",
        string? name = "alice",
        string[]? roles = null,
        TimeSpan? lifetime = null)
    {
        long exp = (now + (lifetime ?? TimeSpan.FromHours(1))).ToUnixTimeSeconds();
        long iat = now.ToUnixTimeSeconds();

        StringBuilder builder = new();
        builder.Append('{');
        builder.Append($"\"sub\":\"{subject}\"");
        if (issuer is not null)
        {
            builder.Append($",\"iss\":\"{issuer}\"");
        }

        if (audience is not null)
        {
            builder.Append($",\"aud\":\"{audience}\"");
        }

        if (name is not null)
        {
            builder.Append($",\"name\":\"{name}\"");
        }

        if (roles is { Length: > 0 })
        {
            builder.Append(",\"roles\":[");
            for (int i = 0; i < roles.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append($"\"{roles[i]}\"");
            }

            builder.Append(']');
        }

        builder.Append($",\"iat\":{iat},\"exp\":{exp}");
        builder.Append('}');
        return builder.ToString();
    }

    private static string BuildHeader(string algorithm, string? keyId)
        => keyId is null
            ? $"{{\"alg\":\"{algorithm}\",\"typ\":\"JWT\"}}"
            : $"{{\"alg\":\"{algorithm}\",\"typ\":\"JWT\",\"kid\":\"{keyId}\"}}";

    private static string Segment(string json) => Encode(Encoding.UTF8.GetBytes(json));

    private static string Encode(byte[] bytes) => Base64Url.EncodeToString(bytes);
}

/// <summary>A test <see cref="TimeProvider"/> with a fixed instant.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

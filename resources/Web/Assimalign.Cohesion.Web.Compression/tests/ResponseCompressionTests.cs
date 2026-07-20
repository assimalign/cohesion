using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using SysHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Compression.Tests;

/// <summary>
/// Full-pipeline coverage of <c>UseResponseCompression</c> over the in-memory HTTP/1.1 factory: real
/// transport, real Content-Length synthesis. The client is configured with no automatic
/// decompression so each test observes the exact wire representation and verifies fidelity by
/// decompressing the body itself.
/// </summary>
public class ResponseCompressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: gzip-accepting client gets a gzip-coded body that round-trips")]
    public async Task UseResponseCompression_GzipAccepted_CompressesAndRoundTrips()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteJson(factory, CompressionPayloads.LargeJson);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        CompressionPayloads.Utf8(CompressionPayloads.GzipDecompress(body)).ShouldBe(CompressionPayloads.LargeJson);
        body.Length.ShouldBeLessThan(CompressionPayloads.Utf8(CompressionPayloads.LargeJson).Length);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: a br-accepting client gets a Brotli-coded body")]
    public async Task UseResponseCompression_BrotliAccepted_UsesBrotli()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteJson(factory, CompressionPayloads.LargeJson);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "br", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldContain("br");
        CompressionPayloads.Utf8(CompressionPayloads.BrotliDecompress(body)).ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: a body below the threshold is sent uncompressed but still carries Vary")]
    public async Task UseResponseCompression_BelowThreshold_DoesNotCompressButVaries()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteJson(factory, CompressionPayloads.SmallJson);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip", cancellation.Token);
        string body = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        body.ShouldBe(CompressionPayloads.SmallJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: an ineligible media type is neither compressed nor varied")]
    public async Task UseResponseCompression_IneligibleMediaType_DoesNothing()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        byte[] payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = "image/png";
            await context.Response.Body.WriteAsync(payload.AsMemory(), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldBeEmpty();
        body.ShouldBe(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: a client sending no Accept-Encoding gets identity, with Vary on the eligible type")]
    public async Task UseResponseCompression_NoAcceptEncoding_ServesIdentityWithVary()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteJson(factory, CompressionPayloads.LargeJson);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, acceptEncoding: null, cancellation.Token);
        string body = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        body.ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: an already-encoded response is not double-compressed")]
    public async Task UseResponseCompression_AlreadyEncoded_HandsOff()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        byte[] preEncoded = CompressionPayloads.GzipCompress(CompressionPayloads.Utf8(CompressionPayloads.LargeJson));
        factory.Application.UseResponseCompression();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
            context.Response.Headers[HttpHeaderKey.ContentEncoding] = "gzip";
            await context.Response.Body.WriteAsync(preEncoded.AsMemory(), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert — exactly one gzip coding (not re-applied), and the bytes are the handler's own.
        response.Content.Headers.ContentEncoding.Count.ShouldBe(1);
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
        body.ShouldBe(preEncoded);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: a higher client q for gzip overrides the server's Brotli-first order")]
    public async Task UseResponseCompression_ClientPrefersGzip_SelectsGzip()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteJson(factory, CompressionPayloads.LargeJson);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip;q=1.0, br;q=0.5", cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: a small body is compressed anyway when the client refuses identity")]
    public async Task UseResponseCompression_IdentityRefusedBelowThreshold_CompressesAnyway()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteJson(factory, CompressionPayloads.SmallJson);
        using HttpClient client = factory.CreateClient();

        // Act — identity;q=0 leaves no uncompressed fallback, so the threshold cannot apply.
        using HttpResponseMessage response = await SendAsync(client, "gzip, identity;q=0", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
        CompressionPayloads.Utf8(CompressionPayloads.GzipDecompress(body)).ShouldBe(CompressionPayloads.SmallJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: a handler that disables the feature is served uncompressed")]
    public async Task UseResponseCompression_FeatureDisabled_SkipsCompression()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        factory.Application.Use(async (context, next) =>
        {
            context.Features.Get<IResponseCompressionFeature>()?.Disable();
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
            await context.Response.Body.WriteAsync(CompressionPayloads.Utf8(CompressionPayloads.LargeJson).AsMemory(), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip", cancellation.Token);
        string body = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        body.ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Response: Accept-Encoding is appended to an existing Vary without clobbering it")]
    public async Task UseResponseCompression_ExistingVary_AppendsAcceptEncoding()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.Vary] = "Accept";
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
            await context.Response.Body.WriteAsync(CompressionPayloads.Utf8(CompressionPayloads.LargeJson).AsMemory(), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "gzip", cancellation.Token);

        // Assert
        response.Headers.Vary.ShouldContain("Accept");
        response.Headers.Vary.ShouldContain("Accept-Encoding");
    }

    private static void WriteJson(WebApplicationTestFactory factory, string json)
    {
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
            await context.Response.Body.WriteAsync(CompressionPayloads.Utf8(json).AsMemory(), context.RequestCancelled);
        });
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string? acceptEncoding, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(SysHttpMethod.Get, "/");
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        return client.SendAsync(request, cancellationToken);
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Compression.Internal;
using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Testing;

using SysHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Compression.Tests;

/// <summary>
/// Proxy-awareness coverage (#1050): the BREACH guard reads the effective scheme. End-to-end cases put
/// the real forwarded-headers middleware in front of <c>UseResponseCompression</c> over
/// <see cref="WebApplicationTestFactory"/>, whose in-memory peer is trusted as a local transport; unit
/// cases run the same middleware over <see cref="TestHttpContext"/> with a plaintext request from an
/// explicit proxy address. The client does no automatic decompression, so the wire representation is
/// observed as sent.
/// </summary>
public class ResponseCompressionForwardedTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);
    private static readonly IPEndPoint _untrustedPeer = new(IPAddress.Parse("198.51.100.7"), 51000);

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Forwarded: A trusted TLS-terminating proxy's https should trip the BREACH guard")]
    public async Task UseResponseCompression_TrustedProxyForwardsHttps_ShouldNotCompress()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseResponseCompression();
        WriteLargeJson(factory);
        using HttpClient client = factory.CreateClient();

        // Act — plaintext on the wire; the proxy relays over TLS to the client.
        using HttpResponseMessage response = await SendAsync(client, forwardedProto: "https", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert — served as identity, and without a Vary it would not otherwise need.
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldNotContain("Accept-Encoding");
        CompressionPayloads.Utf8(body).ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Forwarded: EnableForHttps should compress behind a trusted TLS-terminating proxy")]
    public async Task UseResponseCompression_TrustedProxyForwardsHttpsWithOptIn_ShouldCompress()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseResponseCompression(options => options.EnableForHttps = true);
        WriteLargeJson(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, forwardedProto: "https", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
        CompressionPayloads.Utf8(CompressionPayloads.GzipDecompress(body)).ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Forwarded: Without UseForwardedHeaders a forwarded https header should not switch compression off")]
    public async Task UseResponseCompression_WithoutForwardedHeaders_ShouldCompressOnTransportScheme()
    {
        // Arrange — no forwarded-headers middleware: the effective scheme is the wire scheme (http).
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseResponseCompression();
        WriteLargeJson(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, forwardedProto: "https", cancellation.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.Content.Headers.ContentEncoding.ShouldContain("gzip");
        CompressionPayloads.Utf8(CompressionPayloads.GzipDecompress(body)).ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Forwarded: A plaintext request from a trusted proxy address forwarding https should not be compressed")]
    public async Task InvokeAsync_TrustedProxyAddressForwardsHttps_ShouldNotCompress()
    {
        // Arrange
        TestHttpContext context = CreateProxiedRequest(_trustedProxy);
        MemoryStream body = (MemoryStream)context.Response.Body;

        // Act
        await RunBehindForwardedHeadersAsync(context);

        // Assert — the wire scheme stayed http; the guard judged the effective one.
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentEncoding).ShouldBeFalse();
        CompressionPayloads.Utf8(body.ToArray()).ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Forwarded: An untrusted peer forwarding https should still be compressed")]
    public async Task InvokeAsync_UntrustedPeerForwardsHttps_ShouldCompress()
    {
        // Arrange — the peer is outside KnownProxies, so the trust walk accepts no hop.
        TestHttpContext context = CreateProxiedRequest(_untrustedPeer);
        MemoryStream body = (MemoryStream)context.Response.Body;

        // Act
        await RunBehindForwardedHeadersAsync(context);

        // Assert
        context.Response.Headers[HttpHeaderKey.ContentEncoding].Value.ShouldBe("gzip");
        CompressionPayloads.Utf8(CompressionPayloads.GzipDecompress(body.ToArray())).ShouldBe(CompressionPayloads.LargeJson);
    }

    private static TestHttpContext CreateProxiedRequest(IPEndPoint peer)
    {
        TestHttpContext context = new()
        {
            ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: peer),
        };
        context.Request.Headers[HttpHeaderKey.AcceptEncoding] = "gzip";
        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        context.Request.Headers[HttpHeaderKey.XForwardedProto] = "https";

        return context;
    }

    private static async Task RunBehindForwardedHeadersAsync(TestHttpContext context)
    {
        TestPipelineBuilder builder = new();
        builder.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.KnownProxies.Add(_trustedProxy.Address);
        });
        IWebApplicationMiddleware forwarded = builder.LastMiddleware.ShouldNotBeNull();

        ResponseCompressionMiddleware compression = new(new ResponseCompressionOptions());

        await forwarded.InvokeAsync(context, inner => compression.InvokeAsync(inner, async exchange =>
        {
            exchange.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
            await exchange.Response.Body.WriteAsync(CompressionPayloads.Utf8(CompressionPayloads.LargeJson).AsMemory());
        }));
    }

    private static void WriteLargeJson(WebApplicationTestFactory factory)
    {
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
            await context.Response.Body.WriteAsync(CompressionPayloads.Utf8(CompressionPayloads.LargeJson).AsMemory(), context.RequestCancelled);
        });
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string forwardedProto, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(SysHttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);

        return client.SendAsync(request, cancellationToken);
    }
}

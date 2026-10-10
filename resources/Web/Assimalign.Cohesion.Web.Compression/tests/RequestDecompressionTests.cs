using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using SysHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Compression.Tests;

/// <summary>
/// Full-pipeline coverage of <c>UseRequestDecompression</c> over the in-memory HTTP/1.1 factory: a
/// handler echoes the request body it reads, so the test asserts the handler observed the decoded
/// bytes; the guard and unsupported-coding paths assert the wire status.
/// </summary>
public class RequestDecompressionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a gzip body is decompressed before the handler reads it")]
    public async Task UseRequestDecompression_GzipBody_HandlerReadsDecoded()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] original = CompressionPayloads.Utf8(CompressionPayloads.LargeJson);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        byte[] echoed = await PostAsync(client, CompressionPayloads.GzipCompress(original), "gzip", cancellation.Token);

        // Assert
        echoed.ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a br body is decompressed before the handler reads it")]
    public async Task UseRequestDecompression_BrotliBody_HandlerReadsDecoded()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] original = CompressionPayloads.Utf8(CompressionPayloads.LargeJson);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        byte[] echoed = await PostAsync(client, CompressionPayloads.BrotliCompress(original), "br", cancellation.Token);

        // Assert
        echoed.ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a deflate (zlib) body is decompressed before the handler reads it")]
    public async Task UseRequestDecompression_DeflateBody_HandlerReadsDecoded()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] original = CompressionPayloads.Utf8(CompressionPayloads.LargeJson);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        byte[] echoed = await PostAsync(client, CompressionPayloads.DeflateCompress(original), "deflate", cancellation.Token);

        // Assert
        echoed.ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a chain of codings is decoded in reverse application order")]
    public async Task UseRequestDecompression_MultipleCodings_DecodesChain()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] original = CompressionPayloads.Utf8(CompressionPayloads.LargeJson);
        // Content-Encoding: gzip, br  ->  gzip applied first, then br  ->  wire is br(gzip(original)).
        byte[] wire = CompressionPayloads.BrotliCompress(CompressionPayloads.GzipCompress(original));
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        byte[] echoed = await PostAsync(client, wire, "gzip, br", cancellation.Token);

        // Assert
        echoed.ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: an unsupported coding is refused with 415 before the handler runs")]
    public async Task UseRequestDecompression_UnsupportedCoding_Returns415()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        bool handlerInvoked = false;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        factory.Application.Use(async (context, next) =>
        {
            handlerInvoked = true;
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await PostRawAsync(client, new byte[] { 1, 2, 3, 4 }, "zstd", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.UnsupportedMediaType);
        handlerInvoked.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a body inflating past the guard is rejected with 413")]
    public async Task UseRequestDecompression_ExceedsDecompressedLimit_Returns413()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] bomb = CompressionPayloads.GzipCompress(new byte[100_000]);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression(options => options.MaxDecompressedSizeBytes = 1024);
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await PostRawAsync(client, bomb, "gzip", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.RequestEntityTooLarge);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a malformed coded body is rejected with 400")]
    public async Task UseRequestDecompression_MalformedBody_Returns400()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] notGzip = new byte[64];
        Random.Shared.NextBytes(notGzip);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await PostRawAsync(client, notGzip, "gzip", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a malformed chunked framing under a coded body is the transport's client fault, not relabeled as malformed content")]
    public async Task UseRequestDecompression_MalformedChunkedFraming_ShouldPassTheTransportFailureThrough()
    {
        // Arrange — "zz" is not a chunk size (RFC 9112 §7.1): the transport body under the gzip decoder
        // throws InvalidDataException, latches 400, and answers the exchange itself (#1333, #1340).
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Exception? observed = null;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
        factory.Application.UseRequestDecompression();
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await context.Request.Body.CopyToAsync(Stream.Null, context.RequestCancelled);
            }
            catch (Exception exception)
            {
                observed = exception;
                throw;
            }
        });

        await factory.StartAsync(cancellationToken);

        // Act
        string response = await ExchangeRawAsync(
            transport,
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Encoding: gzip\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\nabc\r\n0\r\n\r\n",
            cancellationToken);

        // Assert — the handler sees the transport's own exception, which a body binder maps like any
        // other malformed body, and the wire carries the transport's 400.
        observed.ShouldBeOfType<InvalidDataException>();
        response.ShouldStartWith("HTTP/1.1 400");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - Request: a body with no Content-Encoding passes through untouched")]
    public async Task UseRequestDecompression_NoContentEncoding_PassesThrough()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        byte[] original = CompressionPayloads.Utf8("plain body, not coded");
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        EchoBody(factory);
        using HttpClient client = factory.CreateClient();

        // Act
        byte[] echoed = await PostAsync(client, original, contentEncoding: null, cancellation.Token);

        // Assert
        echoed.ShouldBe(original);
    }

    /// <summary>
    /// Writes <paramref name="request"/> on one raw connection to <paramref name="transport"/> and reads
    /// until the server closes the connection, returning everything it wrote.
    /// </summary>
    private static async Task<string> ExchangeRawAsync(InMemoryConnectionListener transport, string request, CancellationToken cancellationToken)
    {
        await using Connection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        StringBuilder received = new();
        byte[] buffer = new byte[1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                return received.ToString();
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    private static void EchoBody(WebApplicationTestFactory factory)
    {
        factory.Application.Use(async (context, next) =>
        {
            using MemoryStream buffer = new();
            await context.Request.Body.CopyToAsync(buffer, context.RequestCancelled);
            byte[] received = buffer.ToArray();
            await context.Response.Body.WriteAsync(received.AsMemory(), context.RequestCancelled);
        });
    }

    private static async Task<byte[]> PostAsync(HttpClient client, byte[] body, string? contentEncoding, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostRawAsync(client, body, contentEncoding, cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, byte[] body, string? contentEncoding, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(SysHttpMethod.Post, "/")
        {
            Content = new ByteArrayContent(body),
        };
        if (contentEncoding is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Encoding", contentEncoding);
        }

        return client.SendAsync(request, cancellationToken);
    }
}

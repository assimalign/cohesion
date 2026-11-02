using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Compression.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Compression.Tests;

/// <summary>
/// "Add configures, Use places" (2026-10-28): <c>UseResponseCompression()</c> and
/// <c>UseRequestDecompression()</c> place the middleware the application registered (the last one when
/// it registered several), or one with the defaults when it registered none; and the middleware a
/// builder returned keeps the options it was built from.
/// </summary>
public class CompressionRegistrationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Compression] - UseResponseCompression: Should place the middleware the application registered")]
    public async Task UseResponseCompression_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange — the registration compresses a body the defaults would leave alone: one below 1 KiB.
        TestPipelineBuilder pipeline = new(WriteSmallJsonAsync);
        pipeline.Registered.Add(BuildResponseCompression(options => options.MinimumResponseSizeBytes = 0));
        TestHttpContext context = NewResponseContext("gzip");
        MemoryStream body = (MemoryStream)context.Response.Body;

        // Act
        pipeline.UseResponseCompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.Headers[HttpHeaderKey.ContentEncoding].Value.ShouldBe("gzip");
        CompressionPayloads.Utf8(CompressionPayloads.GzipDecompress(body.ToArray())).ShouldBe(CompressionPayloads.SmallJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - UseResponseCompression: Should place the last registration when compression is registered twice")]
    public async Task UseResponseCompression_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange — gzip only, then Brotli only; the client accepts both.
        TestPipelineBuilder pipeline = new(WriteLargeJsonAsync);
        pipeline.Registered.Add(BuildResponseCompression(options => options.EnableBrotli = false));
        pipeline.Registered.Add(BuildResponseCompression(options => options.EnableGzip = false));
        TestHttpContext context = NewResponseContext("gzip, br");
        MemoryStream body = (MemoryStream)context.Response.Body;

        // Act
        pipeline.UseResponseCompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.Headers[HttpHeaderKey.ContentEncoding].Value.ShouldBe("br");
        CompressionPayloads.Utf8(CompressionPayloads.BrotliDecompress(body.ToArray())).ShouldBe(CompressionPayloads.LargeJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - UseResponseCompression: Should place the defaults when the application registered none")]
    public async Task UseResponseCompression_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange — the defaults offer both codings, prefer Brotli at equal quality, and leave a body
        // below 1 KiB uncompressed.
        TestPipelineBuilder large = new(WriteLargeJsonAsync);
        TestPipelineBuilder small = new(WriteSmallJsonAsync);
        TestHttpContext largeContext = NewResponseContext("gzip, br");
        TestHttpContext smallContext = NewResponseContext("gzip, br");
        MemoryStream largeBody = (MemoryStream)largeContext.Response.Body;
        MemoryStream smallBody = (MemoryStream)smallContext.Response.Body;

        // Act
        large.UseResponseCompression();
        small.UseResponseCompression();
        await large.LastComposed.ShouldNotBeNull().Invoke(largeContext);
        await small.LastComposed.ShouldNotBeNull().Invoke(smallContext);

        // Assert
        largeContext.Response.Headers[HttpHeaderKey.ContentEncoding].Value.ShouldBe("br");
        CompressionPayloads.Utf8(CompressionPayloads.BrotliDecompress(largeBody.ToArray())).ShouldBe(CompressionPayloads.LargeJson);
        smallContext.Response.Headers.ContainsKey(HttpHeaderKey.ContentEncoding).ShouldBeFalse();
        CompressionPayloads.Utf8(smallBody.ToArray()).ShouldBe(CompressionPayloads.SmallJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - ResponseCompressionBuilder: A change to the options after Build should not reach the middleware")]
    public async Task Build_ResponseOptionsChangedAfterBuild_ShouldKeepTheSnapshot()
    {
        // Arrange
        ResponseCompressionBuilder compression = new();
        IWebApplicationMiddleware middleware = compression.Build();
        compression.Options.MinimumResponseSizeBytes = 0;
        compression.Options.EnableGzip = false;
        compression.Options.EnableBrotli = false;

        TestPipelineBuilder pipeline = new(WriteSmallJsonAsync);
        pipeline.Registered.Add(middleware);
        TestHttpContext context = NewResponseContext("gzip");
        MemoryStream body = (MemoryStream)context.Response.Body;

        // Act
        pipeline.UseResponseCompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert — still the 1 KiB threshold it was built with.
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentEncoding).ShouldBeFalse();
        CompressionPayloads.Utf8(body.ToArray()).ShouldBe(CompressionPayloads.SmallJson);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - UseRequestDecompression: Should place the middleware the application registered")]
    public async Task UseRequestDecompression_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange — an 8-byte guard, which the default 100 MiB guard would never trip on 64 bytes.
        BodyReader reader = new();
        TestPipelineBuilder pipeline = new(reader.ReadAsync);
        pipeline.Registered.Add(BuildRequestDecompression(options => options.MaxDecompressedSizeBytes = 8));
        TestHttpContext context = NewRequestContext(CompressionPayloads.GzipCompress(new byte[64]));

        // Act
        pipeline.UseRequestDecompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - UseRequestDecompression: Should place the last registration when decompression is registered twice")]
    public async Task UseRequestDecompression_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange — an 8-byte guard, then the default one.
        BodyReader reader = new();
        TestPipelineBuilder pipeline = new(reader.ReadAsync);
        pipeline.Registered.Add(BuildRequestDecompression(options => options.MaxDecompressedSizeBytes = 8));
        pipeline.Registered.Add(BuildRequestDecompression(options => { }));
        TestHttpContext context = NewRequestContext(CompressionPayloads.GzipCompress(new byte[64]));

        // Act
        pipeline.UseRequestDecompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        reader.Received.ShouldNotBeNull().ShouldBe(new byte[64]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - UseRequestDecompression: Should place the defaults when the application registered none")]
    public async Task UseRequestDecompression_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange
        byte[] original = CompressionPayloads.Utf8(CompressionPayloads.LargeJson);
        BodyReader reader = new();
        TestPipelineBuilder pipeline = new(reader.ReadAsync);
        TestHttpContext context = NewRequestContext(CompressionPayloads.GzipCompress(original));

        // Act
        pipeline.UseRequestDecompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert — decoded before the handler read it, under the default guard.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        reader.Received.ShouldNotBeNull().ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Compression] - RequestDecompressionBuilder: A change to the options after Build should not reach the middleware")]
    public async Task Build_RequestOptionsChangedAfterBuild_ShouldKeepTheSnapshot()
    {
        // Arrange
        RequestDecompressionBuilder decompression = new();
        IWebApplicationMiddleware middleware = decompression.Build();
        decompression.Options.MaxDecompressedSizeBytes = 8;

        BodyReader reader = new();
        TestPipelineBuilder pipeline = new(reader.ReadAsync);
        pipeline.Registered.Add(middleware);
        TestHttpContext context = NewRequestContext(CompressionPayloads.GzipCompress(new byte[64]));

        // Act
        pipeline.UseRequestDecompression();
        await pipeline.LastComposed.ShouldNotBeNull().Invoke(context);

        // Assert — still the default guard it was built with.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        reader.Received.ShouldNotBeNull().ShouldBe(new byte[64]);
    }

    private static IWebApplicationMiddleware BuildResponseCompression(Action<ResponseCompressionOptions> configure)
    {
        ResponseCompressionBuilder compression = new();
        configure.Invoke(compression.Options);

        return compression.Build();
    }

    private static IWebApplicationMiddleware BuildRequestDecompression(Action<RequestDecompressionOptions> configure)
    {
        RequestDecompressionBuilder decompression = new();
        configure.Invoke(decompression.Options);

        return decompression.Build();
    }

    private static TestHttpContext NewResponseContext(string acceptEncoding)
    {
        TestHttpContext context = new();
        context.Request.Headers[HttpHeaderKey.AcceptEncoding] = acceptEncoding;
        return context;
    }

    private static TestHttpContext NewRequestContext(byte[] gzipBody)
    {
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Post;
        context.Request.Headers[HttpHeaderKey.ContentEncoding] = "gzip";
        context.Request.Body = new MemoryStream(gzipBody);
        return context;
    }

    private static async Task WriteLargeJsonAsync(IHttpContext context)
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        await context.Response.Body.WriteAsync(CompressionPayloads.Utf8(CompressionPayloads.LargeJson).AsMemory());
    }

    private static async Task WriteSmallJsonAsync(IHttpContext context)
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        await context.Response.Body.WriteAsync(CompressionPayloads.Utf8(CompressionPayloads.SmallJson).AsMemory());
    }

    /// <summary>A terminal that reads the request body the way a handler would and keeps what it read.</summary>
    private sealed class BodyReader
    {
        public byte[]? Received { get; private set; }

        public async Task ReadAsync(IHttpContext context)
        {
            using MemoryStream buffer = new();
            await context.Request.Body.CopyToAsync(buffer);
            Received = buffer.ToArray();
        }
    }
}

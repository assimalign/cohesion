using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Compression;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;
using Assimalign.Cohesion.Web.RequestTimeouts;
using Assimalign.Cohesion.Web.Rewrite;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// Request capture through the actual decompression, rewrite and timeout middleware, including
/// their shared context state and content-type and capture-limit policies.
/// </summary>
public class HttpLoggingRequestViewTests
{
    private const string bodyText = "{\"value\":\"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ\"}";
    private const int captureLimit = 8;

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Request view: Capture decoded gzip bytes through composed request views")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_DecompressedRequestView_ShouldCaptureDecodedPrefix(bool composeOtherViews)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        byte[] body = Encoding.UTF8.GetBytes(bodyText);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        string? handledPath = null;
        string? handledQuery = null;
        bool requestContextMatches = false;
        bool sawRewrite = false;
        bool sawTimeout = false;
        CancellationToken upstreamCancellation = default;
        CancellationToken handlerCancellation = default;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRequestDecompression();
        if (composeOtherViews)
        {
            factory.Application
                .UseRewrite(rules => rules.AddRewrite("^/original$", "/rewritten?source=rewritten"))
                .UseRequestTimeouts(TimeSpan.FromMinutes(1));
        }
        factory.Application
            .Use((context, next) =>
            {
                upstreamCancellation = context.RequestCancelled;
                return next(context);
            })
            .UseHttpLogging(loggerFactory, options =>
            {
                options.Fields = HttpLoggingFields.RequestBody | HttpLoggingFields.BytesTransferred
                    | HttpLoggingFields.RequestPath | HttpLoggingFields.RequestQuery;
                options.RequestBodyLimit = captureLimit;
            })
            .Use(async (context, next) =>
            {
                handledPath = context.Request.Path.Value;
                handledQuery = context.Request.Query["source"].Value;
                requestContextMatches = ReferenceEquals(context.Request.HttpContext, context);
                handlerCancellation = context.RequestCancelled;
                sawRewrite = context.Features.Get<IWebRewriteFeature>() is not null;
                sawTimeout = context.Features.Get<IRequestTimeoutFeature>() is not null;
                context.Response.StatusCode = HttpStatusCode.Ok;
                // Reading through the request's context must still observe the logging stream.
                await context.Request.HttpContext.Request.Body.CopyToAsync(context.Response.Body, context.RequestCancelled);
            });
        using HttpClient client = factory.CreateClient();
        using ByteArrayContent content = new(CompressGzip(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/original?source=inbound", content, cancellation.Token);
        byte[] echoed = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.EnsureSuccessStatusCode();
        echoed.ShouldBe(body);
        requestContextMatches.ShouldBeTrue();
        handlerCancellation.ShouldBe(upstreamCancellation);
        sawRewrite.ShouldBe(composeOtherViews);
        sawTimeout.ShouldBe(composeOtherViews);
        handledPath.ShouldBe(composeOtherViews ? "/rewritten" : "/original");
        handledQuery.ShouldBe(composeOtherViews ? "rewritten" : "inbound");
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.RequestBody].ShouldBe(bodyText[..captureLimit]);
        entry.Attributes[HttpLoggingAttributes.RequestBodyBytes].ShouldBe((long)body.Length);
        entry.Attributes[HttpLoggingAttributes.ResponseBodyBytes].ShouldBe((long)body.Length);
        entry.Attributes[HttpLoggingAttributes.RequestPath].ShouldBe(handledPath);
        entry.Attributes[HttpLoggingAttributes.RequestQuery].ShouldBe("source=" + handledQuery);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Request view: Capture a body after rewriting while retaining the rewritten URL")]
    public async Task InvokeAsync_RewrittenRequestView_ShouldCaptureBodyAndRewrittenUrl()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        byte[] body = Encoding.UTF8.GetBytes(bodyText);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        string? handledPath = null;
        string? handledQuery = null;
        string? originalPath = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseRewrite(rules => rules.AddRewrite("^/original$", "/rewritten?source=rewritten"))
            .UseHttpLogging(loggerFactory, options =>
            {
                options.Fields = HttpLoggingFields.RequestBody | HttpLoggingFields.BytesTransferred
                    | HttpLoggingFields.RequestPath | HttpLoggingFields.RequestQuery;
                options.RequestBodyLimit = captureLimit;
            })
            .Use(async (context, next) =>
            {
                handledPath = context.Request.Path.Value;
                handledQuery = context.Request.Query["source"].Value;
                originalPath = context.Features.Get<IWebRewriteFeature>()?.OriginalPath.Value;
                context.Response.StatusCode = HttpStatusCode.Ok;
                await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestCancelled);
            });
        using HttpClient client = factory.CreateClient();
        using StringContent content = new(bodyText, Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/original?source=inbound", content, cancellation.Token);
        byte[] echoed = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.EnsureSuccessStatusCode();
        echoed.ShouldBe(body);
        handledPath.ShouldBe("/rewritten");
        handledQuery.ShouldBe("rewritten");
        originalPath.ShouldBe("/original");
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.RequestBody].ShouldBe(bodyText[..captureLimit]);
        entry.Attributes[HttpLoggingAttributes.RequestBodyBytes].ShouldBe((long)body.Length);
        entry.Attributes[HttpLoggingAttributes.RequestPath].ShouldBe("/rewritten");
        entry.Attributes[HttpLoggingAttributes.RequestQuery].ShouldBe("source=rewritten");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Request view: Count bytes without capturing disabled or nonloggable bodies")]
    [InlineData(false, "application/json")]
    [InlineData(true, "application/octet-stream")]
    public async Task InvokeAsync_RewrittenBodyWithoutCapture_ShouldCountAllBytes(bool captureRequested, string contentType)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        byte[] body = Encoding.UTF8.GetBytes(bodyText);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseRewrite(rules => rules.AddRewrite("^/original$", "/rewritten"))
            .UseHttpLogging(loggerFactory, options =>
            {
                options.Fields = HttpLoggingFields.BytesTransferred
                    | (captureRequested ? HttpLoggingFields.RequestBody : HttpLoggingFields.None);
                options.RequestBodyLimit = captureLimit;
            })
            .Use(async (context, next) =>
            {
                context.Response.StatusCode = HttpStatusCode.Ok;
                await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestCancelled);
            });
        using HttpClient client = factory.CreateClient();
        using ByteArrayContent content = new(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        // Act
        using HttpResponseMessage response = await client.PostAsync("/original", content, cancellation.Token);
        byte[] echoed = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        response.EnsureSuccessStatusCode();
        echoed.ShouldBe(body);
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.RequestBodyBytes].ShouldBe((long)body.Length);
        entry.Attributes[HttpLoggingAttributes.ResponseBodyBytes].ShouldBe((long)body.Length);
        entry.Attributes.ContainsKey(HttpLoggingAttributes.RequestBody).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.ResponseBody).ShouldBeFalse();
    }

    private static byte[] CompressGzip(byte[] body)
    {
        using MemoryStream output = new();
        using (GZipStream compressor = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(body);
        }
        return output.ToArray();
    }
}

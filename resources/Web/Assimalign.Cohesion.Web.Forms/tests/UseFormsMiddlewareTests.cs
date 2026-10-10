using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Forms.Tests;

/// <summary>
/// Verifies that <c>UseForms()</c> wires an <see cref="IHttpFormFeature"/> into
/// the request pipeline so downstream middleware can read
/// <c>context.Request.Form</c>, and that a form the parse rejects is answered
/// <c>413</c> or <c>400</c> as problem+json without running the rest of the
/// pipeline. The pipeline is composed with a minimal
/// test-double builder that mirrors the real
/// <c>WebApplication</c> composition (register-order execution) without pulling
/// in the hosting/DI stack.
/// </summary>
public class UseFormsMiddlewareTests
{
    private const string problemJson = "application/problem+json";

    // A multipart section whose header line has no colon: malformed, and over no limit.
    private const string malformedMultipart =
        "--B\r\n" +
        "this is not a header\r\n" +
        "\r\n" +
        "value" +
        "\r\n--B--\r\n";

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: installs the form feature and exposes request.Form downstream")]
    public async Task UseForms_InstallsFeatureAndExposesFormToDownstreamMiddleware()
    {
        TestHttpContext context = new(
            "application/x-www-form-urlencoded",
            BodyOf("name=alice&role=admin"));

        TestPipelineBuilder builder = new();
        builder.UseForms();

        IHttpFormCollection? downstream = null;
        builder.Use((ctx, next) =>
        {
            downstream = ctx.Request.Form;
            return next.Invoke(ctx);
        });

        await builder.Build().ExecuteAsync(context);

        context.Features.Get<IHttpFormFeature>().ShouldNotBeNull();
        downstream.ShouldNotBeNull();
        downstream!["name"].Value.ShouldBe("alice");
        downstream["role"].Value.ShouldBe("admin");
        context.Request.Form["name"].Value.ShouldBe("alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: keeps a pre-installed feature instead of replacing it")]
    public async Task UseForms_WhenFeatureAlreadyInstalled_ShouldNotReplaceIt()
    {
        // A pre-attached collection over a body that would throw if read proves
        // UseForms reuses the installed feature and never touches the stream.
        TestHttpContext context = new("application/x-www-form-urlencoded", new ThrowingStream());
        HttpFormCollection prebuilt = new();
        prebuilt.Add("name", "cohesion");
        HttpFormFeature installed = new(prebuilt);
        context.Features.Set<IHttpFormFeature>(installed);

        TestPipelineBuilder builder = new();
        builder.UseForms();

        await builder.Build().ExecuteAsync(context);

        context.Features.Get<IHttpFormFeature>().ShouldBeSameAs(installed);
        context.Request.Form.ShouldBeSameAs(prebuilt);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: a form over a default limit should be answered 413 problem+json and short-circuit")]
    public async Task UseForms_FormOverDefaultLimit_ShouldAnswer413AndShortCircuit()
    {
        // Arrange — a urlencoded key one character over the default 2,048-character key limit.
        TestHttpContext context = new(
            "application/x-www-form-urlencoded",
            BodyOf(new string('k', 2049) + "=v"));

        int downstream = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(() => downstream++);

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert — RFC 9110 §15.5.14: the client must send less.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe(problemJson);
        downstream.ShouldBe(0);

        using JsonDocument problem = context.ReadProblem();
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(413);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("The request form exceeds a configured size limit.");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: a form over the limit of a pre-installed feature should be answered 413")]
    public async Task UseForms_FormOverInstalledFeatureLimit_ShouldAnswer413()
    {
        // Arrange — the application installed a feature with its own limits ahead of UseForms.
        TestHttpContext context = new("application/x-www-form-urlencoded", BodyOf("a=1&b=2&c=3"));
        context.Features.Set<IHttpFormFeature>(new HttpFormFeature(context.Request, new HttpFormOptions { ValueCountLimit = 2 }));

        int downstream = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(() => downstream++);

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe(problemJson);
        downstream.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: a malformed form should be answered 400 problem+json and short-circuit")]
    public async Task UseForms_MalformedMultipartForm_ShouldAnswer400AndShortCircuit()
    {
        // Arrange — a multipart section whose header line has no colon.
        TestHttpContext context = new("multipart/form-data; boundary=B", BodyOf(malformedMultipart));

        int downstream = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(() => downstream++);

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert — the payload a form-bound endpoint writes for the same failure.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe(problemJson);
        downstream.ShouldBe(0);

        using JsonDocument problem = context.ReadProblem();
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("One or more binding errors occurred.");
        JsonElement errors = problem.RootElement.GetProperty("errors").GetProperty("$form");
        errors.GetArrayLength().ShouldBe(1);
        errors[0].GetString().ShouldBe("The request form could not be read.");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: a rejection after the head was committed should abort the exchange")]
    public async Task UseForms_RejectionAfterResponseStarted_ShouldAbortExchange()
    {
        // Arrange — a middleware ahead of UseForms already committed the response head.
        TestHttpContext context = new("multipart/form-data; boundary=B", BodyOf(malformedMultipart));
        context.Features.Set<IHttpResponseStreamingFeature>(new StartedResponseStreamingFeature());

        int downstream = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(() => downstream++);

        // Act
        await pipeline.ExecuteAsync(context);

        // Assert — no status change and no problem body appended to the committed response.
        context.CancelRequested.ShouldBeTrue();
        downstream.ShouldBe(0);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ResponseBody.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - UseForms: a body read failure that is not a form error should propagate")]
    public async Task UseForms_BodyReadFailsWithIOException_ShouldPropagate()
    {
        // Arrange — a broken connection is the transport's to answer, not a malformed form.
        TestHttpContext context = new("application/x-www-form-urlencoded", new ThrowingStream(new IOException("The connection was reset.")));

        int downstream = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(() => downstream++);

        // Act / Assert
        await Should.ThrowAsync<IOException>(() => pipeline.ExecuteAsync(context));
        downstream.ShouldBe(0);
        context.ResponseBody.Length.ShouldBe(0);
    }

    private static IWebApplicationPipeline BuildPipeline(Action onDownstream)
    {
        TestPipelineBuilder builder = new();
        builder.UseForms();
        builder.Use((ctx, next) =>
        {
            onDownstream();
            return next.Invoke(ctx);
        });

        return builder.Build();
    }

    private static MemoryStream BodyOf(string content) => new(Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes the
    /// registered middleware in registration order — the same shape the real
    /// <c>WebApplication</c> builder produces.
    /// </summary>
    private sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
    {
        private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

        public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        {
            _middleware.Add(middleware);
            return this;
        }

        public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
            => Use(next => context => middleware.InvokeAsync(context, next));

        public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
            => throw new NotSupportedException();

        public IWebApplicationPipeline Build()
        {
            WebApplicationMiddleware pipeline = _ => Task.CompletedTask;
            for (int i = _middleware.Count - 1; i >= 0; i--)
            {
                pipeline = _middleware[i].Invoke(pipeline);
            }

            return new TestPipeline(pipeline);
        }
    }

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }

    /// <summary>
    /// Stream that fails if read: proves a pre-attached form never touches the body, or stands in for a
    /// body whose read fails in the transport.
    /// </summary>
    private sealed class ThrowingStream : Stream
    {
        private readonly Exception? _failure;

        public ThrowingStream(Exception? failure = null) => _failure = failure;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw _failure ?? new InvalidOperationException("Body must not be read when a form is pre-attached.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A response-streaming feature reporting a committed head, as the streaming package would after a
    /// streamed write by a middleware ahead of <c>UseForms</c>.
    /// </summary>
    private sealed class StartedResponseStreamingFeature : IHttpResponseStreamingFeature
    {
        public string Name => nameof(IHttpResponseStreamingFeature);
        public bool HasStarted => true;
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestHttpContext : IHttpContext
    {
        private readonly TestHttpResponse _response;

        public TestHttpContext(string? contentType = null, Stream? body = null)
        {
            Request = new TestHttpRequest(this, contentType, body ?? Stream.Null);
            _response = new TestHttpResponse(this);
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request { get; }
        public IHttpResponse Response => _response;
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;

        /// <summary>Gets whether the middleware aborted the exchange.</summary>
        public bool CancelRequested { get; private set; }

        /// <summary>Gets the bytes written to the response body.</summary>
        public MemoryStream ResponseBody => _response.Captured;

        public void Cancel() => CancelRequested = true;

        public Task CancelAsync()
        {
            CancelRequested = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Parses the problem+json body the middleware wrote.</summary>
        public JsonDocument ReadProblem() => JsonDocument.Parse(_response.Captured.ToArray());
    }

    private sealed class TestHttpRequest : IHttpRequest
    {
        public TestHttpRequest(IHttpContext context, string? contentType, Stream body)
        {
            HttpContext = context;
            Body = body;
            if (contentType is not null)
            {
                Headers[HttpHeaderKey.ContentType] = contentType;
            }
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Post;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; }
    }

    private sealed class TestHttpResponse : IHttpResponse
    {
        public TestHttpResponse(IHttpContext context)
        {
            HttpContext = context;
            Body = Captured;
        }

        public MemoryStream Captured { get; } = new();
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; }
    }
}

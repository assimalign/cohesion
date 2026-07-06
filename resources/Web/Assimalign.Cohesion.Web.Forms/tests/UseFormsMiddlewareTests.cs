using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Forms.Tests;

/// <summary>
/// Verifies that <c>UseForms()</c> wires an <see cref="IHttpFormFeature"/> into
/// the request pipeline so downstream middleware can read
/// <c>context.Request.Form</c>. The pipeline is composed with a minimal
/// test-double builder that mirrors the real
/// <c>WebApplication</c> composition (register-order execution) without pulling
/// in the hosting/DI stack.
/// </summary>
public class UseFormsMiddlewareTests
{
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

    /// <summary>Stream that fails if read, proving a pre-attached form never touches the body.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Body must not be read when a form is pre-attached.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TestHttpContext : IHttpContext
    {
        public TestHttpContext(string? contentType = null, Stream? body = null)
        {
            Request = new TestHttpRequest(this, contentType, body ?? Stream.Null);
            Response = new TestHttpResponse(this);
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;
        public void Cancel()
        {
            // Test double: form parsing never cancels the exchange.
        }
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        public TestHttpResponse(IHttpContext context) => HttpContext = context;

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }
}

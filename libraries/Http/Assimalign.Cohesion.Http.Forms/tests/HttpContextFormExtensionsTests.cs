using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

/// <summary>
/// Smoke tests for the read-only <c>request.Form</c> extension property.
/// The richer setter / <c>ReadFormAsync</c> surface that previously lived
/// here was removed when the package began porting the full ASP.NET Core
/// FormFeature; tests for the new surface will be added back once that
/// port lands (FormReader / KeyValueAccumulator are excluded from the
/// build today and live in-tree as a checkpoint).
/// </summary>
public class HttpContextFormExtensionsTests
{
    [Fact]
    public void Form_NoFeatureAttached_ShouldReturnEmptyCollection()
    {
        IHttpContext context = new BareHttpContext();

        IHttpFormCollection form = context.Request.Form;

        form.ShouldNotBeNull();
        form.Count.ShouldBe(0);
    }

    [Fact]
    public void Form_PreInstalledFeatureWithoutForm_ShouldReturnEmptyCollection()
    {
        IHttpContext context = new BareHttpContext();
        context.Features.Set<IHttpFormFeature>(new TestFormFeature());

        IHttpFormCollection form = context.Request.Form;

        form.Count.ShouldBe(0);
    }

    [Fact]
    public void Form_PreInstalledFeatureWithFormAttached_ShouldReturnInstalledCollection()
    {
        IHttpContext context = new BareHttpContext();
        HttpFormCollection installed = new();
        installed.Add("name", "cohesion");
        context.Features.Set<IHttpFormFeature>(new TestFormFeature { Form = installed });

        IHttpFormCollection observed = context.Request.Form;

        observed.ShouldBeSameAs(installed);
    }

    [Fact]
    public void Form_GetOnNullRequest_ShouldThrow()
    {
        IHttpRequest request = null!;

        Should.Throw<ArgumentNullException>(() => _ = request.Form);
    }

    private sealed class TestFormFeature : IHttpFormFeature
    {
        public string Name => nameof(TestFormFeature);

        public IHttpFormCollection? Form { get; set; }

        public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Form ??= new HttpFormCollection();
            return Task.FromResult(Form);
        }
    }

    private sealed class BareHttpContext : IHttpContext
    {
        public BareHttpContext()
        {
            Request = new BareHttpRequest(this);
            Response = new BareHttpResponse(this);
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
            // Bare double: form parsing never cancels the exchange.
        }
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BareHttpRequest : IHttpRequest
    {
        public BareHttpRequest(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Get;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body => Stream.Null;
    }

    private sealed class BareHttpResponse : IHttpResponse
    {
        public BareHttpResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }
}

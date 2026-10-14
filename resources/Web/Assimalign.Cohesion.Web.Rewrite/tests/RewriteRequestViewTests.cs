using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// The request view a rewrite hands the rest of the pipeline (Web ADR 1): the rewritten path and query, every
/// other member the original's, the original values kept in <see cref="IWebRewriteFeature"/> while the rest
/// of the pipeline runs, and the original context left as it was for the middleware ahead of the rewrite.
/// </summary>
public class RewriteRequestViewTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - View: The rest of the pipeline should see the rewritten path and query and the original everything else")]
    public async Task UseRewrite_Rewrite_ShouldHandDownstreamARequestView()
    {
        // Arrange
        IHttpContext? downstream = null;
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/products/(\\d+)$", "/product?id=$1"));
        builder.Run(context =>
        {
            downstream = context;
            return Task.CompletedTask;
        });

        TestHttpContext original = new("/products/7", "ref=home", HttpScheme.Https, "shop.example");
        original.Request.Method = HttpMethod.Post;
        original.Request.Headers["X-Trace"] = "abc";
        original.Items["key"] = "value";

        // Act
        await builder.SendAsync(original);

        // Assert — rewritten
        downstream.ShouldNotBeNull();
        downstream.ShouldNotBeSameAs(original);
        downstream.Request.Path.Value.ShouldBe("/product");
        downstream.Request.Query.Count.ShouldBe(1);
        downstream.Request.Query["id"].Value.ShouldBe("7");

        // Assert — everything else forwards to the exchange the middleware received
        downstream.Request.Method.ShouldBe(HttpMethod.Post);
        downstream.Request.Scheme.ShouldBe(HttpScheme.Https);
        downstream.Request.Host.Value.ShouldBe("shop.example");
        downstream.Request.Headers.ShouldBeSameAs(original.Request.Headers);
        downstream.Request.Trailers.ShouldBeSameAs(original.Request.Trailers);
        downstream.Request.Body.ShouldBeSameAs(original.Request.Body);
        downstream.Request.HttpContext.ShouldBeSameAs(downstream);
        downstream.Response.ShouldBeSameAs(original.Response);
        downstream.Features.ShouldBeSameAs(original.Features);
        downstream.Items.ShouldBeSameAs(original.Items);
        downstream.ConnectionInfo.ShouldBeSameAs(original.ConnectionInfo);
        downstream.Version.ShouldBe(original.Version);
        downstream.RequestCancelled.ShouldBe(original.RequestCancelled);

        // Assert — the original request is untouched
        original.Request.Path.Value.ShouldBe("/products/7");
        original.Request.Query["ref"].Value.ShouldBe("home");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - View: Cancelling through the view should cancel the exchange")]
    public async Task UseRewrite_CancelThroughView_ShouldCancelOriginal()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/a$", "/b"));
        builder.Run(context => context.CancelAsync());
        TestHttpContext original = new("/a");

        // Act
        await builder.SendAsync(original);

        // Assert
        original.Cancelled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Feature: The originals should be readable while the rest of the pipeline runs, and gone after")]
    public async Task UseRewrite_Rewrite_ShouldPublishOriginalsOnlyWhileDownstreamRuns()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/old/(.*)$", "/new/$1?v=2"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("/old/page", "v=1");
        IHttpQueryCollection originalQuery = context.Request.Query;

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Rewrite.ShouldNotBeNull();
        probe.Rewrite.OriginalPath.Value.ShouldBe("/old/page");
        probe.Rewrite.OriginalQuery.ShouldBeSameAs(originalQuery);
        probe.Path!.Value.Value.ShouldBe("/new/page");
        probe.Query!["v"].Value.ShouldBe("2");
        context.Features.Get<IWebRewriteFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Feature: A second rewrite should keep the values the client sent")]
    public async Task UseRewrite_TwoRewriteMiddlewares_ShouldKeepFirstOriginals()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/a$", "/b?step=1"));
        builder.UseRewrite(rules => rules.AddRewrite("^/b$", "/c?step=2"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("/a", "client=1");

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Path!.Value.Value.ShouldBe("/c");
        probe.Query!["step"].Value.ShouldBe("2");
        probe.Rewrite!.OriginalPath.Value.ShouldBe("/a");
        probe.Rewrite.OriginalQuery["client"].Value.ShouldBe("1");
        context.Features.Get<IWebRewriteFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Feature: A fault downstream should still remove the feature")]
    public async Task UseRewrite_DownstreamThrows_ShouldRemoveFeature()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/a$", "/b"));
        builder.Run(_ => throw new InvalidOperationException("downstream fault"));
        TestHttpContext context = new("/a");

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => builder.SendAsync(context));

        // Assert
        context.Features.Get<IWebRewriteFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Pitfall: Middleware ahead of the rewrite should keep seeing the original values after next returns")]
    public async Task UseRewrite_MiddlewareAheadOfRewrite_ShouldSeeOriginalValues()
    {
        // Arrange — the documented pitfall: a component that holds its context across next sees the original.
        string? before = null;
        string? afterNext = null;
        string? inside = null;
        TestPipelineBuilder builder = new();
        builder.Use(async (IHttpContext context, WebApplicationMiddleware next) =>
        {
            before = context.Request.Path.Value;
            await next(context);
            afterNext = context.Request.Path.Value;
        });
        builder.UseRewrite(rules => rules.AddRewrite("^/old$", "/new"));
        builder.Use(async (IHttpContext context, WebApplicationMiddleware next) =>
        {
            inside = context.Request.Path.Value;
            await next(context);
        });
        builder.Run(_ => Task.CompletedTask);

        // Act
        await builder.SendAsync(new TestHttpContext("/old"));

        // Assert
        before.ShouldBe("/old");
        inside.ShouldBe("/new");
        afterNext.ShouldBe("/old");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - View: A view wrapping another view should keep its request members")]
    public async Task UseRewrite_InnerRequestView_ShouldForwardToIt()
    {
        // Arrange — an earlier view (a decompressed body, for example) stays in effect under the rewrite.
        MemoryStream decoded = new();
        IHttpContext? downstream = null;
        TestPipelineBuilder builder = new();
        builder.Use(next => context => next(new BodyView(context, decoded)));
        builder.UseRewrite(rules => rules.AddRewrite("^/a$", "/b"));
        builder.Run(context =>
        {
            downstream = context;
            return Task.CompletedTask;
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/a"));

        // Assert
        downstream!.Request.Path.Value.ShouldBe("/b");
        downstream.Request.Body.ShouldBeSameAs(decoded);
    }

    // A stand-in for another request view (Web.Compression's decompressed body).
    private sealed class BodyView : IHttpContext
    {
        private readonly IHttpContext _inner;

        public BodyView(IHttpContext inner, Stream body)
        {
            _inner = inner;
            Request = new BodyRequest(inner.Request, body, this);
        }

        public HttpVersion Version => _inner.Version;

        public IHttpRequest Request { get; }

        public IHttpResponse Response => _inner.Response;

        public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;

        public IHttpFeatureCollection Features => _inner.Features;

        public IDictionary<string, object?> Items => _inner.Items;

        public CancellationToken RequestCancelled => _inner.RequestCancelled;

        public void Cancel() => _inner.Cancel();

        public Task CancelAsync() => _inner.CancelAsync();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class BodyRequest : IHttpRequest
    {
        private readonly IHttpRequest _inner;

        public BodyRequest(IHttpRequest inner, Stream body, IHttpContext context)
        {
            _inner = inner;
            Body = body;
            HttpContext = context;
        }

        public HttpHost Host => _inner.Host;

        public HttpPath Path => _inner.Path;

        public HttpMethod Method => _inner.Method;

        public HttpScheme Scheme => _inner.Scheme;

        public IHttpQueryCollection Query => _inner.Query;

        public IHttpHeaderCollection Headers => _inner.Headers;

        public IHttpContext HttpContext { get; }

        public Stream Body { get; }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

public class HttpContextFormExtensionsTests
{
    [Fact]
    public void Form_BeforeSetOrRead_ShouldReturnNull()
    {
        IHttpContext context = new BareHttpContext();

        IHttpFormCollection? form = context.Request.Form;

        form.ShouldBeNull();
    }

    [Fact]
    public void Form_BeforeSet_ShouldNotInstallFeatureOnRead()
    {
        // The getter must remain side-effect-free: reading Form on a context
        // that has never seen form middleware should leave the feature
        // collection untouched so callers can still test for the presence of
        // an IHttpFormFeature later.
        IHttpContext context = new BareHttpContext();

        _ = context.Request.Form;

        context.Features.Get<IHttpFormFeature>().ShouldBeNull();
    }

    [Fact]
    public void Form_Set_ShouldRoundTripViaGetter()
    {
        IHttpContext context = new BareHttpContext();
        HttpFormCollection installed = new();
        installed.Add("name", "cohesion");

        context.Request.Form = installed;

        context.Request.Form.ShouldBeSameAs(installed);
        context.Request.Form!["name"].Value.ShouldBe("cohesion");
    }

    [Fact]
    public void Form_Set_ShouldInstallFormFeature()
    {
        IHttpContext context = new BareHttpContext();
        HttpFormCollection installed = new();

        context.Request.Form = installed;

        IHttpFormFeature? feature = context.Features.Get<IHttpFormFeature>();
        feature.ShouldNotBeNull();
        feature!.Form.ShouldBeSameAs(installed);
    }

    [Fact]
    public void Form_SetTwice_ShouldReuseFeatureInstance()
    {
        // Subsequent assignments mutate the existing IHttpFormFeature
        // rather than churning a new one into the collection. This matters for
        // observers that captured the feature reference earlier in the pipeline.
        IHttpContext context = new BareHttpContext();
        context.Request.Form = new HttpFormCollection();
        IHttpFormFeature firstFeature = context.Features.Get<IHttpFormFeature>()!;

        HttpFormCollection second = new();
        context.Request.Form = second;
        IHttpFormFeature secondFeature = context.Features.Get<IHttpFormFeature>()!;

        secondFeature.ShouldBeSameAs(firstFeature);
        secondFeature.Form.ShouldBeSameAs(second);
    }

    [Fact]
    public void Form_SetNull_ShouldThrow()
    {
        IHttpContext context = new BareHttpContext();

        Should.Throw<ArgumentNullException>(() => context.Request.Form = null!);
    }

    [Fact]
    public void Form_GetOnNullRequest_ShouldThrow()
    {
        IHttpRequest request = null!;

        Should.Throw<ArgumentNullException>(() => _ = request.Form);
    }

    [Fact]
    public void Form_SetOnNullRequest_ShouldThrow()
    {
        IHttpRequest request = null!;
        HttpFormCollection installed = new();

        Should.Throw<ArgumentNullException>(() => request.Form = installed);
    }

    [Fact]
    public void Form_PreInstalledFeature_ShouldBeObservedByGetter()
    {
        // Middleware that installs the feature directly (rather than via the
        // Form setter) must still be visible through context.Form.
        IHttpContext context = new BareHttpContext();
        HttpFormCollection collection = new();
        context.Features.Set<IHttpFormFeature>(new TestFormFeature { Form = collection });

        IHttpFormCollection? observed = context.Request.Form;

        observed.ShouldBeSameAs(collection);
    }

    [Fact]
    public async Task ReadFormAsync_WithPreAttachedCollection_ShouldReturnIt()
    {
        IHttpContext context = new BareHttpContext();
        HttpFormCollection installed = new();
        installed.Add("name", "cohesion");
        context.Request.Form = installed;

        IHttpFormCollection resolved = await context.ReadFormAsync();

        resolved.ShouldBeSameAs(installed);
        resolved["name"].Value.ShouldBe("cohesion");
    }

    [Fact]
    public async Task ReadFormAsync_NoFeatureAttached_ShouldReturnEmptyAndCache()
    {
        // PR-1 scaffold behaviour: ReadFormAsync produces an empty collection and caches it
        // for subsequent calls. A future PR ports the multipart parser into this package.
        IHttpContext context = new BareHttpContext();

        IHttpFormCollection first = await context.ReadFormAsync();
        IHttpFormCollection second = await context.ReadFormAsync();

        first.Count.ShouldBe(0);
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task ReadFormAsync_NoFeatureAttached_ShouldInstallFeatureAndCacheForm()
    {
        // The first ReadFormAsync call should install an IHttpFormFeature so a
        // subsequent context.Form read returns the same parsed collection.
        IHttpContext context = new BareHttpContext();

        IHttpFormCollection parsed = await context.ReadFormAsync();

        context.Features.Get<IHttpFormFeature>().ShouldNotBeNull();
        context.Request.Form.ShouldBeSameAs(parsed);
    }

    [Fact]
    public async Task ReadFormAsync_CancelledToken_ShouldThrow()
    {
        IHttpContext context = new BareHttpContext();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => context.ReadFormAsync(cts.Token));
    }

    [Fact]
    public async Task ReadFormAsync_NullContext_ShouldThrow()
    {
        IHttpContext context = null!;

        await Should.ThrowAsync<ArgumentNullException>(
            () => context.ReadFormAsync());
    }

    /// <summary>
    /// Test-local <see cref="IHttpFormFeature"/> stand-in. Used to verify that
    /// the extension getter consults the feature collection rather than only
    /// recognizing the package's internal default implementation.
    /// </summary>
    private sealed class TestFormFeature : IHttpFormFeature
    {
        public IHttpFormCollection? Form { get; set; }

        public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Form ??= new HttpFormCollection();
            return Task.FromResult(Form);
        }
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpContext"/> stub. The <see cref="Request"/> and
    /// <see cref="Response"/> are real <see cref="BareHttpRequest"/> /
    /// <see cref="BareHttpResponse"/> instances that hold a back-reference to
    /// this context so the <c>context.Request.Form</c> extension property and
    /// <c>context.ReadFormAsync(...)</c> extension method &#8211; both of which
    /// resolve through <see cref="IHttpRequest.HttpContext"/> &#8211; have a
    /// real feature collection to work against.
    /// </summary>
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
        public IHttpProtocolUpgrade? Upgrade => null;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestAborted => CancellationToken.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpRequest"/> stub used by <see cref="BareHttpContext"/>.
    /// Only the back-reference to the owning <see cref="IHttpContext"/> matters
    /// here &#8211; the rest of the surface returns inert defaults so the
    /// interface contract is satisfied.
    /// </summary>
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
        public IHttpCookieCollection Cookies { get; } = new HttpCookieCollection();
        public IHttpContext HttpContext { get; }
        public System.IO.Stream Body => System.IO.Stream.Null;
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpResponse"/> stub used by <see cref="BareHttpContext"/>.
    /// </summary>
    private sealed class BareHttpResponse : IHttpResponse
    {
        public BareHttpResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpCookieCollection Cookies { get; } = new HttpCookieCollection();
        public IHttpContext HttpContext { get; }
        public System.IO.Stream Body { get; set; } = System.IO.Stream.Null;
    }
}

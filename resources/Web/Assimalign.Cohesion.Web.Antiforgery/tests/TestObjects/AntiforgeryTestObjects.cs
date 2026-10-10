using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

/// <summary>
/// An <see cref="IWebApplicationBuilder"/> that records the features registered on it, so the antiforgery
/// feature can be composed without the hosting stack and its registration inspected.
/// </summary>
internal sealed class TestWebApplicationBuilder : IWebApplicationBuilder
{
    public List<IHttpFeature> Features { get; } = new();

    /// <summary>Gets the antiforgery service of the last antiforgery feature registered.</summary>
    public IHttpAntiforgery Antiforgery => Features.OfType<IHttpAntiforgeryFeature>().Last().Antiforgery;

    public IWebApplicationBuilder AddFeature(IHttpFeature feature)
    {
        Features.Add(feature);
        return this;
    }

    public IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure) => throw new NotSupportedException();

    public IWebApplicationBuilder AddServer(IWebApplicationServer server) => throw new NotSupportedException();

    public IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server) => throw new NotSupportedException();

    public IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline) => throw new NotSupportedException();

    public IWebApplication Build() => throw new NotSupportedException();
}

/// <summary>
/// The application context a pipeline is composed with, carrying the features registered at builder time
/// — what <c>UseAntiforgery</c> reads when the pipeline is built.
/// </summary>
internal sealed class TestWebApplicationContext : IWebApplicationContext
{
    public TestWebApplicationContext(IEnumerable<IHttpFeature> features)
    {
        Features = features.ToArray();
    }

    public FileSystemPath? ContentRootPath => null;
    public FileSystemPath? WebRootPath => null;
    public IEnumerable<IWebApplicationMiddleware> Middleware => Array.Empty<IWebApplicationMiddleware>();
    public IEnumerable<IWebApplicationServer> Servers => Array.Empty<IWebApplicationServer>();
    public IEnumerable<IHttpFeature> Features { get; }
}

/// <summary>
/// A pipeline builder that composes middleware in registration order around a terminal, with an
/// application context for the context-aware <c>Use</c> overload — the shape the real
/// <c>WebApplication</c> produces, without the hosting/DI stack.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();
    private readonly IWebApplicationContext _context;
    private readonly WebApplicationMiddleware _terminal;

    public TestPipelineBuilder(IWebApplicationContext context, WebApplicationMiddleware terminal)
    {
        _context = context;
        _terminal = terminal;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use((_, next) => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => Use((_, next) => middleware(next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = _terminal;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i](_context, pipeline);
        }

        return new TestPipeline(pipeline);
    }

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _pipeline;

        public TestPipeline(WebApplicationMiddleware pipeline) => _pipeline = pipeline;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default) => _pipeline(context);
    }
}

/// <summary>
/// A mutable <see cref="IHttpContext"/> double: settable method, real header and feature collections (so
/// the cookie, form and antiforgery extension paths work), a request body stream, and a capturing
/// response body.
/// </summary>
internal sealed class AntiforgeryTestContext : IHttpContext
{
    private readonly CancellationTokenSource _requestCancelled = new();

    public AntiforgeryTestContext(HttpMethod method, byte[]? body = null)
    {
        Request = new TestHttpRequest(this, method, new MemoryStream(body ?? Array.Empty<byte>()));
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => _requestCancelled.Token;

    /// <summary>Gets whether the exchange was aborted.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>Gets the request body stream, to observe whether the middleware read it.</summary>
    public MemoryStream RequestBody => (MemoryStream)Request.Body;

    public void Cancel()
    {
        CancelRequested = true;
        _requestCancelled.Cancel();
    }

    public Task CancelAsync()
    {
        Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _requestCancelled.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Sets the <c>Cookie</c> request header to <c>name=value</c>.</summary>
    public AntiforgeryTestContext WithCookie(string name, string value)
    {
        Request.Headers[HttpHeaderKey.Cookie] = $"{name}={value}";
        return this;
    }

    /// <summary>Sets a request header.</summary>
    public AntiforgeryTestContext WithHeader(string name, string value)
    {
        Request.Headers[name] = value;
        return this;
    }

    /// <summary>Gets the response body written so far, as UTF-8 text.</summary>
    public string ReadResponseBody() => Encoding.UTF8.GetString(((MemoryStream)Response.Body).ToArray());

    /// <summary>Creates a context whose body is a URL-encoded form.</summary>
    public static AntiforgeryTestContext ForUrlEncodedForm(HttpMethod method, string form)
    {
        AntiforgeryTestContext context = new(method, Encoding.UTF8.GetBytes(form));
        context.Request.Headers[HttpHeaderKey.ContentType] = "application/x-www-form-urlencoded";
        return context;
    }

    private sealed class TestHttpRequest : IHttpRequest
    {
        public TestHttpRequest(IHttpContext context, HttpMethod method, Stream body)
        {
            HttpContext = context;
            Method = method;
            Body = body;
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method { get; }
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
        public Stream Body { get; set; } = new MemoryStream();
    }
}

/// <summary>
/// A convention builder that records the metadata attached to it, to observe what the verbs append.
/// </summary>
internal sealed class RecordingConventionBuilder : IRouterConventionBuilder
{
    public List<object> Items { get; } = new();

    public IRouterConventionBuilder WithMetadata(params object[] items)
    {
        Items.AddRange(items);
        return this;
    }
}

/// <summary>
/// A stand-in for the endpoint <c>UseRouting</c> publishes: installing it ahead of the antiforgery
/// middleware is what routing does before calling <c>next</c>. Set <see cref="IsPreflight"/> to model the
/// candidate endpoint routing publishes for a CORS preflight.
/// </summary>
internal sealed class FakeRouteMatchFeature : IRouteMatchFeature
{
    private readonly IRouterRouteMetadataCollection _metadata;

    public FakeRouteMatchFeature(params object[] metadata) => _metadata = new RouterRouteMetadataCollection(metadata);

    public string Name => nameof(IRouteMatchFeature);
    public bool IsPreflight { get; init; }
    public IRouterRoute? Route => null;
    public RouteValueDictionary? Values => null;
    public IRouterRouteMetadataCollection Metadata => _metadata;
}

/// <summary>
/// A response-streaming feature reporting a started (head-committed) response, as the streaming package
/// would after a streamed write by a middleware ahead of antiforgery.
/// </summary>
internal sealed class FakeResponseStreamingFeature : IHttpResponseStreamingFeature
{
    public string Name => nameof(IHttpResponseStreamingFeature);
    public bool HasStarted => true;
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// An in-memory <see cref="IKeyRepository"/>: two providers created over one instance share a key ring,
/// the way two application instances share a key directory.
/// </summary>
internal sealed class InMemoryKeyRepository : IKeyRepository
{
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public IReadOnlyList<KeyDocument> GetAllKeys()
        => _store.Select(entry => new KeyDocument(entry.Key, (byte[])entry.Value.Clone())).ToList();

    public void StoreKey(KeyDocument key) => _store[key.Name] = key.Content.ToArray();
}

/// <summary>
/// Adapts a data-protection protector to the antiforgery seam the way the package's internal adapter does,
/// so a test can mint tokens under a purpose chain of its choosing.
/// </summary>
internal sealed class TestDataProtectionProtector : IHttpAntiforgeryProtector
{
    private readonly IDataProtector _protector;

    public TestDataProtectionProtector(IDataProtector protector) => _protector = protector;

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => _protector.Protect(plaintext);

    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, [NotNullWhen(true)] out byte[]? plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(protectedData);
            return true;
        }
        catch (DataProtectionException)
        {
            plaintext = null;
            return false;
        }
    }
}

/// <summary>
/// A protector that records its use, to prove which protector a service was built with.
/// </summary>
internal sealed class CountingProtector : IHttpAntiforgeryProtector
{
    public int ProtectCount { get; private set; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        ProtectCount++;
        return plaintext.ToArray();
    }

    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, [NotNullWhen(true)] out byte[]? plaintext)
    {
        plaintext = protectedData.ToArray();
        return true;
    }
}

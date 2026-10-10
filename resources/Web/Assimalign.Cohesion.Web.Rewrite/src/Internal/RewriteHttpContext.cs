using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// The <see cref="IHttpContext"/> the rewrite middleware hands the rest of the pipeline: a pass-through view
/// whose <see cref="Request"/> carries the rewritten path and query, while every other member forwards to
/// the exchange it wraps, so the response, features, items and cancellation never fork.
/// </summary>
/// <remarks>
/// <see cref="IHttpRequest.Path"/> and <see cref="IHttpRequest.Query"/> are get-only and transport-owned, so
/// a rewrite cannot change them in place; this view is how the rewritten URL reaches routing, static files,
/// the generated endpoint binders and every other reader without any of them changing (Web ADR 1). It is the
/// pattern Web.Compression (<c>RequestDecompressionHttpContext</c>) and Web.RequestTimeouts
/// (<c>RequestTimeoutHttpContext</c>) established.
/// </remarks>
internal sealed class RewriteHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly RewriteHttpRequest _request;

    public RewriteHttpContext(IHttpContext inner, HttpPath path, IHttpQueryCollection query)
    {
        _inner = inner;
        _request = new RewriteHttpRequest(inner.Request, path, query, this);
    }

    public HttpVersion Version => _inner.Version;

    public IHttpRequest Request => _request;

    public IHttpResponse Response => _inner.Response;

    public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;

    public IHttpFeatureCollection Features => _inner.Features;

    public IDictionary<string, object?> Items => _inner.Items;

    public CancellationToken RequestCancelled => _inner.RequestCancelled;

    public void Cancel() => _inner.Cancel();

    public Task CancelAsync() => _inner.CancelAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

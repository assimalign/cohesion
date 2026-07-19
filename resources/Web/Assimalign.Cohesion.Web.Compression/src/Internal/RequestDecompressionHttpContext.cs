using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// The <see cref="IHttpContext"/> the request-decompression middleware hands downstream: a
/// pass-through decorator whose <see cref="Request"/> exposes the decompressed body while every
/// other member forwards to the real exchange, so shared response state, features, and the
/// cancellation token never fork.
/// </summary>
internal sealed class RequestDecompressionHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly RequestDecompressionHttpRequest _request;

    public RequestDecompressionHttpContext(IHttpContext inner, Stream body)
    {
        _inner = inner;
        _request = new RequestDecompressionHttpRequest(inner.Request, body, this);
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

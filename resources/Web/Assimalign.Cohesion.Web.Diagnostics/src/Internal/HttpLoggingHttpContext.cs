using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Presents the capture stream to the downstream pipeline without mutating the incoming request.
/// All exchange state and lifetime operations remain owned by the original context.
/// </summary>
internal sealed class HttpLoggingHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly HttpLoggingHttpRequest _request;

    public HttpLoggingHttpContext(IHttpContext inner, Stream body)
    {
        _inner = inner;
        _request = new HttpLoggingHttpRequest(inner.Request, body, this);
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

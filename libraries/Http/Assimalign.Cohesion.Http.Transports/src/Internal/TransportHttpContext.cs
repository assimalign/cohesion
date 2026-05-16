using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpContext : HttpContext
{
    protected TransportHttpContext(
        HttpVersion version,
        HttpRequest request,
        HttpResponse response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted)
    {
        Version = version;
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo;
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        RequestAborted = requestAborted;
    }

    public override HttpVersion Version { get; }

    public override HttpRequest Request { get; }

    public override HttpResponse Response { get; }

    public override HttpConnectionInfo ConnectionInfo { get; }

    public override HttpFeatureCollection Features { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestAborted { get; }

    public override ValueTask DisposeAsync()
    {
        Request.Body.Dispose();
        Response.Body.Dispose();

        return ValueTask.CompletedTask;
    }
}

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
        IHttpConnectionInfo connectionInfo,
        CancellationToken requestAborted)
    {
        Version = version;
        Session = new HttpSession();
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo;
        RequestAborted = requestAborted;
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);
    }

    public override HttpVersion Version { get; }

    public override HttpSession Session { get; }

    public override HttpRequest Request { get; }

    public override HttpResponse Response { get; }

    public override IHttpConnectionInfo ConnectionInfo { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestAborted { get; }

    public override ValueTask DisposeAsync()
    {
        Request.Body.Dispose();
        Response.Body.Dispose();

        return ValueTask.CompletedTask;
    }
}

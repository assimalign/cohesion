using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

public abstract class HttpConnection : IHttpConnection
{
    internal HttpConnection(ITransportConnection connection, bool isSecure)
    {
        Connection = connection;
        IsSecure = isSecure;
    }

    protected ITransportConnection Connection { get; }

    protected bool IsSecure { get; }
    public ConnectionId Id => Connection.Id;
    public TransportId TransportId => Connection.TransportId;
    public TransportProtocol Protocol => TransportProtocol.Http;
    public ConnectionState State => Connection.State;

    public void Abort()
    {
        Connection.Abort();
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        return Connection.AbortAsync(cancellationToken);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public abstract ValueTask DisposeAsync();

    public abstract HttpConnectionContext Open();

    public abstract ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);

    IHttpConnectionContext IHttpConnection.Open()
    {
        return Open();
    }

    async ValueTask<IHttpConnectionContext> IHttpConnection.OpenAsync(CancellationToken cancellationToken)
    {
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}

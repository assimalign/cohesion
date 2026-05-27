using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

public abstract class HttpConnection : TransportConnection<HttpConnectionContext>, IHttpConnection
{
    internal HttpConnection(
        ITransportConnection connection,
        bool isSecure,
        Func<IHttpFeatureCollection>? createFeatures)
    {
        Connection = connection;
        IsSecure = isSecure;
        CreateFeatures = createFeatures;
    }

    protected ITransportConnection Connection { get; }

    protected bool IsSecure { get; }

    /// <summary>
    /// Optional factory invoked once per <see cref="IHttpContext"/>
    /// produced from this connection to create the request-scoped
    /// <see cref="IHttpFeatureCollection"/>. <see langword="null"/> when
    /// no <see cref="HttpConnectionListenerOptions.CreateFeatures"/>
    /// hook was configured.
    /// </summary>
    protected Func<IHttpFeatureCollection>? CreateFeatures { get; }

    public override ConnectionId Id => Connection.Id;
    public override TransportId TransportId => Connection.TransportId;
    public override TransportProtocol Protocol => TransportProtocol.Http;
    public override ConnectionState State => Connection.State;

    public override void Abort()
    {
        Connection.Abort();
    }

    public override ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        return Connection.AbortAsync(cancellationToken);
    }

    public override void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public abstract override ValueTask DisposeAsync();

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

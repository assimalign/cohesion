using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public abstract class ServerTransport : ITransport
{
    /// <inheritdoc />
    public TransportType TransportType => TransportType.Server;

    /// <inheritdoc />
    public abstract ProtocolType ProtocolType { get; }

    /// <inheritdoc />
    public abstract TransportMiddlewareHandler Middleware { get; }

    /// <summary>
    /// Accepts or Listens for incoming connections.
    /// </summary>
    /// <remarks>
    /// Protocols like UDP listen for incoming data before initiating a connection 
    /// where as TCP will accept a connection before listening for incoming data.
    /// </remarks>
    /// <returns></returns>
    public abstract Task<ITransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);


    /// <inheritdoc />
    public abstract void Dispose();


    ITransportConnection ITransport.Initialize() => AcceptOrListenAsync().GetAwaiter().GetResult();
    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken) => await AcceptOrListenAsync(cancellationToken);
}

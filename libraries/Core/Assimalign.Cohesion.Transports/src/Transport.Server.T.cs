using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class ServerTransport<TConnection> : ITransport
    where TConnection : ITransportConnection
{
    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Server;

    /// <inheritdoc />
    public abstract ProtocolType Protocol { get; }

    /// <summary>
    /// Accepts or Listens for incoming connections.
    /// </summary>
    /// <remarks>
    /// Protocols like UDP listen for incoming data before initiating a connection 
    /// where as TCP will accept a connection before listening for incoming data.
    /// </remarks>
    /// <returns></returns>
    public abstract Task<TConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract void Dispose();


    ITransportConnection ITransport.Initialize() => AcceptOrListenAsync().GetAwaiter().GetResult();
    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken) => await AcceptOrListenAsync(cancellationToken);
}

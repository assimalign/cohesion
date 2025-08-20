using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
public abstract class ServerTransport : ITransport
{
    protected ServerTransport()
    {
        Id = TransportId.New();
    }

    /// <inheritdoc />
    public virtual TransportId Id { get; }

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Server;

    /// <inheritdoc />
    public abstract TransportProtocol Protocol { get; }

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
    public virtual void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    ITransportConnection ITransport.Initialize() => AcceptOrListenAsync().GetAwaiter().GetResult();
    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken) => await AcceptOrListenAsync(cancellationToken);
}

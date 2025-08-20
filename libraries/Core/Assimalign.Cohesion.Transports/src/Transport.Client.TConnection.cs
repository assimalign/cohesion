using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class ClientTransport<TConnection> : ITransport
    where TConnection : ITransportConnection
{
    protected ClientTransport()
    {
        Id = TransportId.New();
    }

    /// <inheritdoc />
    public virtual TransportId Id { get; }

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Client;

    /// <inheritdoc />
    public abstract TransportProtocol Protocol { get; }

    

    /// <summary>
    /// A method that connects to a remote host (server) and returns a <see cref="ITransportConnection"/> object.
    /// </summary>
    /// <returns></returns>
    public abstract Task<TConnection> ConnectAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    ITransportConnection ITransport.Initialize() => ConnectAsync().GetAwaiter().GetResult();
    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken) => await ConnectAsync(cancellationToken);
}

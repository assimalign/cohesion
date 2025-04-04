using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class ClientTransport<TConnection> : ITransport
    where TConnection : ITransportConnection
{
    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Client;

    /// <inheritdoc />
    public abstract ProtocolType Protocol { get; }

    /// <summary>
    /// A method that connects to a remote host (server) and returns a <see cref="ITransportConnection"/> object.
    /// </summary>
    /// <returns></returns>
    public abstract Task<TConnection> ConnectAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract void Dispose();


    ITransportConnection ITransport.Initialize() => ConnectAsync().GetAwaiter().GetResult();
    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken) => await ConnectAsync(cancellationToken);
}

using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;


/// <summary>
/// Represents a transport to be used for an underlying client.
/// </summary>
public abstract class ClientTransport : ITransport
{
    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Client;

    /// <inheritdoc />
    public abstract ProtocolType Protocol { get; }

    /// <summary>
    /// A method that connects to a remote host (server) and returns a <see cref="ITransportConnection"/> object.
    /// </summary>
    /// <returns></returns>
    public abstract Task<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract void Dispose();



    ITransportConnection ITransport.Initialize() => ConnectAsync().GetAwaiter().GetResult();
    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken) => await ConnectAsync(cancellationToken);
}

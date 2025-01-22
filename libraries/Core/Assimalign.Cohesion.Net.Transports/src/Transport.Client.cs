using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;


/// <summary>
/// Represents a transport to be used for an underlying client.
/// </summary>
public abstract class ClientTransport : ITransport
{
    /// <inheritdoc />
    public TransportType TransportType => TransportType.Client;

    /// <inheritdoc />
    public abstract ProtocolType ProtocolType { get; }

    /// <inheritdoc />
    public abstract TransportMiddlewareHandler Middleware { get; }

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

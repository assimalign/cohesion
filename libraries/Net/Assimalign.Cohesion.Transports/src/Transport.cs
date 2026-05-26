using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class Transport : ITransport
{
    protected Transport()
    {
        Id = TransportId.New();
    }

    /// <summary>
    /// A unique identifier for the transport.
    /// </summary>
    /// <remarks>
    /// Useful when a server is listening on multiple transports, each transport will have a unique identifier.
    /// </remarks>
    public virtual TransportId Id { get; }

    /// <summary>
    /// Specifies whether the transport is a client or server.
    /// </summary>
    public abstract TransportKind Kind { get; }

    /// <summary>
    /// The underlying network protocol of the transport.
    /// </summary>
    public abstract TransportProtocol Protocol { get; }

    /// <summary>
    /// Either accepts incoming connection or connects to remote host.
    /// </summary>
    /// <returns><see cref="ITransportConnection"/></returns>
    protected virtual TransportConnection Initialize() => InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// Either accepts incoming connection or connects to remote host.
    /// </summary>
    /// <remarks>
    /// It's best to execute middleware inside initialization block.
    /// </remarks>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Task{ITransportConnection}"/></returns>
    protected abstract Task<TransportConnection> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    public void Dispose() => DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public abstract ValueTask DisposeAsync();

    ITransportConnection ITransport.Initialize()
    {
        return Initialize();
    }

    async Task<ITransportConnection> ITransport.InitializeAsync(CancellationToken cancellationToken)
    {
        return await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}

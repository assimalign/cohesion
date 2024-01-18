using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

/// <summary>
/// A <see cref="ITransport"/> represents either a client or server 
/// for an underlying network protocol.
/// </summary>
/// <remarks>
/// A transport represents a delivery system for data transfer from host to host. It is used
/// to make higher level transports such as IP for TCP and UDP or application layer protocols like HTTP.
/// </remarks>
public interface ITransport : IDisposable
{
    /// <summary>
    /// Specifies whether the transport is a client or server.
    /// </summary>
    TransportType TransportType { get; }
    /// <summary>
    /// The underlying network protocol of the transport.
    /// </summary>
    ProtocolType ProtocolType { get; }
    /// <summary>
    /// Middleware to be executed on initialization.
    /// </summary>
    TransportMiddlewareHandler Middleware { get; }
    /// <summary>
    /// Either accepts incoming connection or connects to remote host.
    /// </summary>
    /// <returns><see cref="ITransportConnection"/></returns>
    ITransportConnection Initialize();
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// It's best to execute middleware inside initialization block.
    /// </remarks>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Task{ITransportConnection}"/></returns>
    Task<ITransportConnection> InitializeAsync(CancellationToken cancellationToken = default);
}
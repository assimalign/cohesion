using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// A <see cref="ITransport"/> represents either a client or server 
/// for an underlying network protocol.
/// </summary>
/// <remarks>
/// A transport represents a delivery system for data transfer from host to host. It is used
/// to make higher level transports such as IP for TCP and UDP or application layer protocols like HTTP.
/// </remarks>
public interface ITransport : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// A unique identifier for the transport.
    /// </summary>
    /// <remarks>
    /// Useful when a server is listening on multiple transports, each transport will have a unique identifier.
    /// </remarks>
    TransportId Id { get;  }

    /// <summary>
    /// Specifies whether the transport is a client or server.
    /// </summary>
    TransportKind Kind { get; }

    /// <summary>
    /// The underlying network protocol of the transport.
    /// </summary>
    TransportProtocol Protocol { get; }

    /// <summary>
    /// Either accepts incoming connection or connects to remote host.
    /// </summary>
    /// <returns><see cref="ITransportConnection"/></returns>
    ITransportConnection Initialize();

    /// <summary>
    /// Either accepts incoming connection or connects to remote host.
    /// </summary>
    /// <remarks>
    /// It's best to execute middleware inside initialization block.
    /// </remarks>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Task{ITransportConnection}"/></returns>
    Task<ITransportConnection> InitializeAsync(CancellationToken cancellationToken = default);
}
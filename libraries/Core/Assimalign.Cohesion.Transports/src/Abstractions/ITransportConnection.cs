using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
public interface ITransportConnection : IDisposable, IAsyncDisposable 
{
    /// <summary>
    /// A unique connection id.
    /// </summary>
    ConnectionId Id { get; }

    /// <summary>
    /// Get the id of 
    /// </summary>
    TransportId TransportId { get; }

    /// <summary>
    /// The underlying network protocol of the transport connection.
    /// </summary>
    ProtocolType Protocol { get; }

    /// <summary>
    /// Represents the current state of the pipeline.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Aborts the connection.
    /// </summary>
    void Abort();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask AbortAsync(CancellationToken cancellationToken = default);
}
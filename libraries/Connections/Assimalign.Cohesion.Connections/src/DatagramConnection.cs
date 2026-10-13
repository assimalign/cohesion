using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base for a datagram connection, sharing the root's lifecycle and features.
/// </summary>
public abstract class DatagramConnection : Connection, IDatagramConnection
{
    /// <inheritdoc />
    public abstract ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> payload, EndPoint remoteEndPoint, CancellationToken cancellationToken = default);
}

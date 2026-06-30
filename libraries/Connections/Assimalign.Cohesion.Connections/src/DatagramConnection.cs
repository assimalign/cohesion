using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IDatagramConnection"/>.
/// </summary>
public abstract class DatagramConnection : IDatagramConnection
{
    /// <inheritdoc />
    public abstract EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> payload, EndPoint remoteEndPoint, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();
}

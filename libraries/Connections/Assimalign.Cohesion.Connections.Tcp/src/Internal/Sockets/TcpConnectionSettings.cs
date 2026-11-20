using System;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// An immutable per-queue template describing how a <see cref="TcpConnection"/> wires its
/// duplex pipe pair and socket IO event schedulers.
/// </summary>
internal sealed class TcpConnectionSettings
{
    /// <summary>
    /// The longest a graceful <see cref="TcpConnection.DisposeAsync"/> waits for the send loop to
    /// hand what the application wrote to the socket before it aborts the connection.
    /// </summary>
    public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the longest a graceful close waits for the send loop to drain before it escalates to an
    /// abort. Only tests shorten it; there is no public option (#1086).
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = DefaultCloseTimeout;

    /// <summary>
    /// Gets the socket-specific pipe option context (pipe options, schedulers, and memory-pool
    /// block size) shared by every connection created from this template.
    /// </summary>
    public SocketPipeOptionsContext PipeOptions { get; init; } = default!;

    /// <summary>
    /// Gets a value indicating whether the receive loop should wait for data to be available
    /// before allocating a receive buffer.
    /// </summary>
    public bool WaitForDataBeforeAllocatingBuffer { get; init; }
}

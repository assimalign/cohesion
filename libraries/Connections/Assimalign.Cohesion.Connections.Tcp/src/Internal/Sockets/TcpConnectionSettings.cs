namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// An immutable per-queue template describing how a <see cref="TcpConnection"/> wires its
/// duplex pipe pair and socket IO event schedulers.
/// </summary>
internal sealed class TcpConnectionSettings
{
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

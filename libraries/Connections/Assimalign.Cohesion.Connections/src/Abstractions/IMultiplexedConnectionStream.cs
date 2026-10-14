using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents an independent byte stream carried by a multiplexed connection.
/// </summary>
/// <remarks>
/// Each stream owns its lifecycle and features. Its features do not inherit those of the parent
/// multiplexed connection. Completing <see cref="System.IO.Pipelines.IDuplexPipe.Output"/> cleanly ends only output.
/// </remarks>
public interface IMultiplexedConnectionStream : IDuplexConnection
{
    /// <summary>
    /// Gets whether the peer ended input cleanly and the transport has delivered every received
    /// byte to the input pipe. A reset, abort, or failure is not a clean completion.
    /// </summary>
    /// <remarks>
    /// This property does not read, wait, or consume bytes. False means the clean end is not known
    /// yet. The input pipe may still contain unread bytes, so consumers must also check that its
    /// buffer is empty. A layer that buffers bytes must preserve this distinction.
    /// </remarks>
    bool IsInputCompleted { get; }

    /// <summary>
    /// Resets both directions of the stream with <paramref name="errorCode"/> where the transport
    /// carries a reset code, then ends the stream's lifecycle.
    /// </summary>
    /// <param name="errorCode">The transport-specific application error code sent to the peer when supported.</param>
    /// <param name="reason">An optional exception describing why the stream was aborted.</param>
    /// <remarks>
    /// A direction that has already ended retains its first code. A transport that cannot carry
    /// a reset code resets the whole stream without one. The state becomes
    /// <see cref="ConnectionState.Aborted"/> and <see cref="IConnection.ConnectionClosed"/> is signaled.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="errorCode"/> is negative or outside the range supported by the transport.
    /// </exception>
    void Abort(long errorCode, Exception? reason = null);
}

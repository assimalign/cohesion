using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Aborts a multiplexed connection with an application error code, so the peer learns why the connection
/// ended.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IMultiplexedConnection.Abort(Exception)"/> closes a connection with the transport's configured
/// default code, whatever the reason. A protocol that gives the codes meaning chooses one per call: an HTTP/3
/// connection error closes with the RFC 9114 §8.1 code that names it, such as <c>H3_FRAME_UNEXPECTED</c>. A
/// multiplexed connection whose transport carries a code on its close (QUIC's <c>CONNECTION_CLOSE</c> frame,
/// RFC 9000 §19.19) implements this interface beside its connection contract. A consumer finds it with a type
/// test on the connection it holds, and falls back to <see cref="IMultiplexedConnection.Abort(Exception)"/> on
/// a connection without it.
/// </para>
/// <para>
/// The lifecycle is the one <see cref="IMultiplexedConnection.Abort(Exception)"/> has:
/// <see cref="IMultiplexedConnection.State"/> becomes <see cref="ConnectionState.Aborted"/>,
/// <see cref="IMultiplexedConnection.ConnectionClosed"/> is signaled, and every stream ends. The first abort or
/// disposal decides the close, and a later call has no effect.
/// </para>
/// </remarks>
public interface IMultiplexedConnectionAbort
{
    /// <summary>
    /// Aborts the connection and all of its streams immediately, closing it with <paramref name="errorCode"/>.
    /// </summary>
    /// <param name="errorCode">The application protocol error code the peer receives.</param>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="errorCode"/> is negative or greater than 2^62 - 1, the largest value a QUIC
    /// variable-length integer carries (RFC 9000 §16).
    /// </exception>
    void Abort(long errorCode, Exception? reason = null);
}

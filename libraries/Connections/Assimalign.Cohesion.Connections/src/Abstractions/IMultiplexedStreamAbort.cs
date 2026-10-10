using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Abandons one direction of a stream of a multiplexed connection with an application error code, so an
/// application protocol can tell its peer why it stopped reading or sending.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IConnection.Abort(Exception)"/> ends a whole stream and cannot say why on the wire: a
/// multiplexed transport such as QUIC signals its configured default code. A protocol that gives the codes
/// meaning (HTTP/3, RFC 9114 §8.1) chooses one per call and per direction. A server that has sent a complete
/// response, for example, stops the request direction with <c>H3_NO_ERROR</c> and still ends the response
/// direction gracefully. A stream that can carry the code implements this interface beside its connection
/// contract. A consumer finds it with a type test on the stream it holds, and falls back to
/// <see cref="IConnection.Abort(Exception)"/> on a stream without it.
/// </para>
/// <para>
/// Each direction ends once. The first signal for a direction fixes the code the peer receives, whether it
/// comes from this interface, <see cref="IConnection.Abort(Exception)"/>, or disposal, and a later one does
/// not signal that direction again. To reset a whole stream with a code, abort both directions here, then call
/// <see cref="IConnection.Abort(Exception)"/>, which ends the stream's lifecycle with the directions already
/// carrying the code.
/// </para>
/// <para>
/// Neither member changes <see cref="IConnection.State"/> or signals this stream's
/// <see cref="IConnection.ConnectionClosed"/>: the holder ended a direction itself, so nothing ended underneath
/// it. The peer's stream, though, reports an abandoned stream, as it does for any reset or stop. Completing
/// <see cref="System.IO.Pipelines.IDuplexPipe.Output"/> stays the graceful end of the sending direction.
/// </para>
/// </remarks>
public interface IMultiplexedStreamAbort
{
    /// <summary>
    /// Abandons the receiving direction: data not yet read is discarded, and the peer is asked to stop sending
    /// with <paramref name="errorCode"/> (a QUIC <c>STOP_SENDING</c> frame, RFC 9000 §19.5).
    /// </summary>
    /// <remarks>
    /// A read in flight and every later read fail, including a read that octets the transport has already
    /// received and buffered would satisfy. The sending direction is unaffected. The call has no effect
    /// when the receiving direction has already ended: the stream is write-only, its input was completed, it
    /// was aborted or disposed, or its connection is gone.
    /// </remarks>
    /// <param name="errorCode">The application protocol error code the peer receives.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="errorCode"/> is negative or greater than 2^62 - 1, the largest value a QUIC
    /// variable-length integer carries (RFC 9000 §16).
    /// </exception>
    void AbortRead(long errorCode);

    /// <summary>
    /// Abandons the sending direction: data not yet sent is discarded, and the peer is told the stream was
    /// reset with <paramref name="errorCode"/> (a QUIC <c>RESET_STREAM</c> frame, RFC 9000 §19.4).
    /// </summary>
    /// <remarks>
    /// Every later write fails. The receiving direction is unaffected. The call has no effect when the sending
    /// direction has already ended: the stream is read-only, its output was completed, it was aborted or
    /// disposed, or its connection is gone.
    /// </remarks>
    /// <param name="errorCode">The application protocol error code the peer receives.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="errorCode"/> is negative or greater than 2^62 - 1, the largest value a QUIC
    /// variable-length integer carries (RFC 9000 §16).
    /// </exception>
    void AbortWrite(long errorCode);
}

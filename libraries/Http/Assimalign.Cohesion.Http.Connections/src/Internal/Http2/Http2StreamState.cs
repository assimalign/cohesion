namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Lifecycle state of an HTTP/2 stream, as defined by RFC 9113 §5.1.
/// </summary>
/// <remarks>
/// <para>
/// The full state diagram includes <c>reserved (local)</c> and
/// <c>reserved (remote)</c> for server-push (PUSH_PROMISE) flows. Cohesion's
/// HTTP/2 server does not implement push (it advertises
/// <c>SETTINGS_ENABLE_PUSH = 0</c> in its initial SETTINGS), so the reserved
/// states are intentionally omitted — any path that would reach them is
/// instead a protocol error.
/// </para>
/// <para>
/// Transitions on a server, summarised from RFC 9113 §5.1:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Idle"/>: stream id has never been
///   referenced. Only <c>HEADERS</c> and <c>PRIORITY</c> are legal in this
///   state.</description></item>
///   <item><description><see cref="Open"/>: <c>HEADERS</c> has been
///   received without <c>END_STREAM</c>. Both peers may send.</description></item>
///   <item><description><see cref="HalfClosedRemote"/>: the peer (client)
///   has closed its half by sending <c>END_STREAM</c>. The server may still
///   send response frames; the client may only send
///   <c>WINDOW_UPDATE</c>, <c>PRIORITY</c>, or
///   <c>RST_STREAM</c>.</description></item>
///   <item><description><see cref="HalfClosedLocal"/>: the server has sent
///   <c>END_STREAM</c> on its response. Reserved for symmetry; uncommon on
///   the server side where the request body usually arrives before the
///   response is generated.</description></item>
///   <item><description><see cref="Closed"/>: terminal state. Any frame
///   other than <c>PRIORITY</c> is treated as either <c>STREAM_CLOSED</c>
///   (stream error) or <c>PROTOCOL_ERROR</c> (connection error) depending
///   on how the stream was closed.</description></item>
/// </list>
/// </remarks>
internal enum Http2StreamState
{
    /// <summary>The stream id has never been referenced on this connection.</summary>
    Idle = 0,

    /// <summary>HEADERS without END_STREAM has been observed.</summary>
    Open = 1,

    /// <summary>The remote (client) half is closed; the server may still send.</summary>
    HalfClosedRemote = 2,

    /// <summary>The local (server) half is closed; the client may still send.</summary>
    HalfClosedLocal = 3,

    /// <summary>The stream is terminated — gracefully (END_STREAM both ways) or via reset.</summary>
    Closed = 4,
}

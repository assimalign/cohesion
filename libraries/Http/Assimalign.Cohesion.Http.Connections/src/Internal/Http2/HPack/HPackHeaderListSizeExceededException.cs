using System;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

/// <summary>
/// Raised while decoding an HTTP/2 field section when the decoded header list — the running sum of
/// <c>name-length + value-length + 32</c> across the fields (RFC 9113 §10.5.1) — exceeds the
/// server's advertised <c>SETTINGS_MAX_HEADER_LIST_SIZE</c>.
/// </summary>
/// <remarks>
/// Distinguished from a plain <see cref="HPackDecodingException"/> so the connection context can map
/// it to <c>ENHANCE_YOUR_CALM</c> (an excessive-load signal) rather than the <c>PROTOCOL_ERROR</c>
/// used for malformed field sections. Because the decode aborts early — before the whole block has
/// been processed — the HPACK dynamic-table state is left indeterminate, which is why this is a
/// connection-level fault (GOAWAY) rather than a recoverable stream reset.
/// </remarks>
[Serializable]
internal sealed class HPackHeaderListSizeExceededException : HPackDecodingException
{
    public HPackHeaderListSizeExceededException()
    {
    }

    public HPackHeaderListSizeExceededException(string message)
        : base(message)
    {
    }

    public HPackHeaderListSizeExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

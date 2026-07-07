using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

internal enum Http3ErrorCode : long
{
    /// <summary>
    /// H3_NO_ERROR (0x100):
    /// No error. This is used when the connection or stream needs to be closed, but there is no error to signal.
    /// </summary>
    NoError = 0x100,
    /// <summary>
    /// H3_GENERAL_PROTOCOL_ERROR (0x101):
    /// Peer violated protocol requirements in a way which doesn't match a more specific error code,
    /// or endpoint declines to use the more specific error code.
    /// </summary>
    ProtocolError = 0x101,
    /// <summary>
    /// H3_INTERNAL_ERROR (0x102):
    /// An internal error has occurred in the HTTP stack.
    /// </summary>
    InternalError = 0x102,
    /// <summary>
    ///  H3_STREAM_CREATION_ERROR (0x103):
    /// The endpoint detected that its peer created a stream that it will not accept.
    /// </summary>
    StreamCreationError = 0x103,
    /// <summary>
    /// H3_CLOSED_CRITICAL_STREAM (0x104):
    /// A stream required by the connection was closed or reset.
    /// </summary>
    ClosedCriticalStream = 0x104,
    /// <summary>
    /// H3_FRAME_UNEXPECTED (0x105):
    /// A frame was received which was not permitted in the current state.
    /// </summary>
    UnexpectedFrame = 0x105,
    /// <summary>
    /// H3_FRAME_ERROR (0x106):
    /// A frame that fails to satisfy layout requirements or with an invalid size was received.
    /// </summary>
    FrameError = 0x106,
    /// <summary>
    /// H3_EXCESSIVE_LOAD (0x107):
    /// The endpoint detected that its peer is exhibiting a behavior that might be generating excessive load.
    /// </summary>
    ExcessiveLoad = 0x107,
    /// <summary>
    /// H3_ID_ERROR (0x109):
    /// A Stream ID, Push ID, or Placeholder ID was used incorrectly, such as exceeding a limit, reducing a limit, or being reused.
    /// </summary>
    IdError = 0x108,
    /// <summary>
    /// H3_SETTINGS_ERROR (0x109):
    /// An endpoint detected an error in the payload of a SETTINGS frame.
    /// </summary>
    SettingsError = 0x109,
    /// <summary>
    /// H3_MISSING_SETTINGS (0x10A):
    /// No SETTINGS frame was received at the beginning of the control stream.
    /// </summary>
    MissingSettings = 0x10a,
    /// <summary>
    /// H3_REQUEST_REJECTED (0x10B):
    /// A server rejected a request without performing any application processing.
    /// </summary>
    RequestRejected = 0x10b,
    /// <summary>
    /// H3_REQUEST_CANCELLED (0x10C):
    /// The request or its response (including pushed response) is cancelled.
    /// </summary>
    RequestCancelled = 0x10c,
    /// <summary>
    /// H3_REQUEST_INCOMPLETE (0x10D):
    /// The client's stream terminated without containing a fully-formed request.
    /// </summary>
    RequestIncomplete = 0x10d,
    /// <summary>
    /// H3_MESSAGE_ERROR (0x10E):
    /// An HTTP message was malformed and cannot be processed.
    /// </summary>
    MessageError = 0x10e,
    /// <summary>
    /// H3_CONNECT_ERROR (0x10F):
    /// The connection established in response to a CONNECT request was reset or abnormally closed.
    /// </summary>
    ConnectError = 0x10f,
    /// <summary>
    /// H3_VERSION_FALLBACK (0x110):
    /// The requested operation cannot be served over HTTP/3. The peer should retry over HTTP/1.1.
    /// </summary>
    VersionFallback = 0x110,

    /// <summary>
    /// QPACK_DECOMPRESSION_FAILED (0x0200):
    /// The decoder failed to interpret an encoded field section and is not able to continue decoding
    /// that field section (RFC 9204 §8.3). Also used when a field section's Required Insert Count is
    /// unsatisfiable or when the peer exceeds the decoder's blocked-stream limit (§2.1.2 / §2.2).
    /// </summary>
    QPackDecompressionFailed = 0x0200,

    /// <summary>
    /// QPACK_ENCODER_STREAM_ERROR (0x0201):
    /// The decoder failed to interpret an encoder instruction received on the QPACK encoder stream
    /// (RFC 9204 §8.3), for example an insert that cannot fit the dynamic table or a capacity that
    /// exceeds the decoder's advertised maximum.
    /// </summary>
    QPackEncoderStreamError = 0x0201,

    /// <summary>
    /// QPACK_DECODER_STREAM_ERROR (0x0202):
    /// The encoder failed to interpret a decoder instruction received on the QPACK decoder stream
    /// (RFC 9204 §8.3).
    /// </summary>
    QPackDecoderStreamError = 0x0202,
}

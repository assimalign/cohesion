namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The type and payload length that open every HTTP/3 frame (RFC 9114 §7.1), as read off a request
/// stream by <see cref="Http3RequestStreamReader"/>. Both are QUIC variable-length integers
/// (RFC 9000 §16), so the length ranges up to 2^62 − 1 and is bounds-checked by the reader against the
/// stream and the configured limits before any payload octet is buffered.
/// </summary>
/// <param name="Type">The frame type.</param>
/// <param name="Length">The payload length, in octets.</param>
internal readonly record struct Http3FrameHeader(long Type, long Length);

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

/// <summary>
/// The result of reading an HTTP/1.1 message body from the wire: the framed body bytes
/// plus any trailers parsed from a chunked encoding. Trailers will be empty for
/// identity-framed or Content-Length-framed messages.
/// </summary>
internal readonly struct Http1MessageBody
{
    public Http1MessageBody(byte[] body, HttpHeaderCollection trailers)
    {
        Body = body;
        Trailers = trailers;
    }

    /// <summary>The decoded body bytes (chunked encoding stripped if present).</summary>
    public byte[] Body { get; }

    /// <summary>
    /// Trailer headers parsed from the chunked trailer section. Empty for
    /// non-chunked-framed messages.
    /// </summary>
    public HttpHeaderCollection Trailers { get; }
}

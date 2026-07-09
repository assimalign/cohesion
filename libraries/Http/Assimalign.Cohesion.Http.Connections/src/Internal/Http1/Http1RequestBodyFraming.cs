namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// How an HTTP/1.1 request body is delimited on the wire, decided from the request headers before
/// the body is read (RFC 9112 §6).
/// </summary>
internal enum Http1RequestBodyMode
{
    /// <summary>No body — neither <c>Content-Length</c> nor <c>Transfer-Encoding</c> is present (RFC 9112 §6.1).</summary>
    None,

    /// <summary>The body is exactly <see cref="Http1RequestBodyFraming.ContentLength"/> octets (RFC 9112 §6.2).</summary>
    ContentLength,

    /// <summary>The body is chunk-framed and self-delimiting (RFC 9112 §7.1).</summary>
    Chunked,
}

/// <summary>
/// The framing decision for an HTTP/1.1 request body: the delimitation mode and, for
/// <see cref="Http1RequestBodyMode.ContentLength"/>, the declared length. Computed from the request
/// headers at head-parse time and handed to <see cref="Http1RequestBodyStream"/>, which performs the
/// actual incremental read.
/// </summary>
internal readonly struct Http1RequestBodyFraming
{
    private Http1RequestBodyFraming(Http1RequestBodyMode mode, long contentLength)
    {
        Mode = mode;
        ContentLength = contentLength;
    }

    /// <summary>The framing mode.</summary>
    public Http1RequestBodyMode Mode { get; }

    /// <summary>The declared body length in octets when <see cref="Mode"/> is <see cref="Http1RequestBodyMode.ContentLength"/>; otherwise zero.</summary>
    public long ContentLength { get; }

    /// <summary>A framing that carries no body.</summary>
    public static Http1RequestBodyFraming None { get; } = new(Http1RequestBodyMode.None, 0);

    /// <summary>A chunk-framed body.</summary>
    public static Http1RequestBodyFraming Chunked { get; } = new(Http1RequestBodyMode.Chunked, 0);

    /// <summary>A Content-Length-framed body of <paramref name="length"/> octets.</summary>
    public static Http1RequestBodyFraming ForContentLength(long length) => new(Http1RequestBodyMode.ContentLength, length);
}

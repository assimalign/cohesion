namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the trailer section of an HTTP message — the field block that may
/// follow the message body (RFC 9110 §6.5). A trailer section is structurally a
/// field section, so this contract extends <see cref="IHttpHeaderCollection"/>
/// and adds only the capability signal.
/// </summary>
/// <remarks>
/// <para>
/// Trailers are part of the core HTTP message model (unlike cookies, sessions,
/// or forms, which are layered application concerns surfaced through features):
/// every HTTP version defines a trailer section — chunked transfer in HTTP/1.1
/// (RFC 9112 §7.1.2) and the trailing HEADERS field section in HTTP/2
/// (RFC 9113 §8.1) and HTTP/3 (RFC 9114 §4.1). They therefore sit on
/// <see cref="IHttpRequest"/> and <see cref="IHttpResponse"/> beside
/// <c>Headers</c> rather than behind a feature.
/// </para>
/// <para>
/// <see cref="IsSupported"/> reports whether the current exchange surfaces a
/// trailer section. When <see langword="false"/>, the collection is empty and
/// mutating it throws — a request/response that cannot carry trailers (for
/// example a non-chunked HTTP/1.1 message) must fail loudly rather than silently
/// accept trailers that can never be transmitted.
/// </para>
/// </remarks>
public interface IHttpTrailerCollection : IHttpHeaderCollection
{
    /// <summary>
    /// Gets a value indicating whether this exchange surfaces a trailer
    /// section. When <see langword="false"/>, the collection is empty and is
    /// read-only (mutation throws).
    /// </summary>
    bool IsSupported { get; }
}

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// The content-coding tokens (RFC 9110 &#167; 8.4.1) this package understands, shared by the
/// response-compression and request-decompression halves so the two directions agree on spelling.
/// </summary>
internal static class ContentCodings
{
    /// <summary>The <c>gzip</c> content coding (RFC 9110 &#167; 8.4.1.3).</summary>
    public const string Gzip = "gzip";

    /// <summary>The <c>br</c> (Brotli) content coding (RFC 7932).</summary>
    public const string Brotli = "br";

    /// <summary>The <c>deflate</c> content coding (RFC 9110 &#167; 8.4.1.2 &#8212; the zlib data format).</summary>
    public const string Deflate = "deflate";

    /// <summary>The <c>identity</c> coding (no transformation), used as the negotiation sentinel.</summary>
    public const string Identity = "identity";
}

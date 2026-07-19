using System.Collections.Generic;
using System.IO.Compression;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Builder-time configuration for response compression, captured when
/// <c>UseResponseCompression</c> is called. Governs which client codings are offered, which
/// response media types are eligible, the size threshold below which a response is left
/// uncompressed, the compression level, and the BREACH-cautious HTTPS opt-in.
/// </summary>
/// <remarks>
/// <para>
/// The middleware negotiates the response coding from the request's <c>Accept-Encoding</c> using
/// the shared <c>Assimalign.Cohesion.Http</c> negotiation primitives; these options only shape the
/// server's side of that negotiation (which codings it will apply, in which preference order) and
/// the eligibility gate (media type, size, status). Nothing here is read at request time beyond the
/// captured values &#8212; there is no service container and no configuration binding.
/// </para>
/// <para>
/// Compression of dynamic content over HTTPS is a BREACH (CVE-2013-3587) exposure, so
/// <see cref="EnableForHttps"/> defaults to <see langword="false"/>: over an <c>https</c> request the
/// middleware serves the response uncompressed unless the flag is explicitly set. See the package
/// <c>docs/DESIGN.md</c>.
/// </para>
/// </remarks>
public sealed class ResponseCompressionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether responses to <c>https</c> requests may be
    /// compressed. Defaults to <see langword="false"/> as a BREACH (CVE-2013-3587) precaution:
    /// compressing attacker-influenced dynamic content alongside a secret over TLS can leak the
    /// secret through compressed-length observation. Enable only when the responses this pipeline
    /// serves do not mix secrets with attacker-controlled input.
    /// </summary>
    public bool EnableForHttps { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the <c>gzip</c> coding may be offered. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool EnableGzip { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the <c>br</c> (Brotli) coding may be offered.
    /// Defaults to <see langword="true"/>. When both codings are acceptable to the client at equal
    /// quality, Brotli is preferred for its better ratio; a higher client <c>q</c> value still wins.
    /// </summary>
    public bool EnableBrotli { get; set; } = true;

    /// <summary>
    /// Gets or sets the compression level applied by the gzip/Brotli encoders. Defaults to
    /// <see cref="CompressionLevel.Fastest"/>, the appropriate trade-off for on-the-fly dynamic
    /// responses where per-request CPU matters more than the last few percent of ratio.
    /// </summary>
    public CompressionLevel Level { get; set; } = CompressionLevel.Fastest;

    /// <summary>
    /// Gets or sets the minimum response size, in bytes, at which compression engages. A response
    /// whose body stays at or below this many bytes is sent uncompressed &#8212; below roughly a
    /// kilobyte the coding's own framing overhead can cancel or exceed the saving. Defaults to
    /// <c>1024</c>. Set to <c>0</c> to compress every eligible response regardless of size.
    /// </summary>
    /// <remarks>
    /// The threshold is honored without buffering the whole response: the middleware buffers only
    /// up to this many bytes to make the decision, then streams the remainder through the encoder.
    /// It does not apply when the client's <c>Accept-Encoding</c> refuses <c>identity</c> (there is
    /// no uncompressed fallback to fall back to), in which case an eligible response is always
    /// compressed.
    /// </remarks>
    public int MinimumResponseSizeBytes { get; set; } = 1024;

    /// <summary>
    /// Gets the set of response media types eligible for compression. An entry is matched against
    /// the response's <c>Content-Type</c> media type (parameters such as <c>charset</c> ignored),
    /// case-insensitively; a subtype wildcard (<c>text/*</c>) or the full wildcard (<c>*/*</c>) is
    /// honored. Seeded with the common text and structured-text types; clear and repopulate to
    /// override.
    /// </summary>
    public IList<string> MimeTypes { get; } = new List<string>
    {
        "text/plain",
        "text/html",
        "text/css",
        "text/xml",
        "text/javascript",
        "text/csv",
        "text/markdown",
        "application/javascript",
        "application/json",
        "application/xml",
        "application/xhtml+xml",
        "application/manifest+json",
        "application/problem+json",
        "application/problem+xml",
        "image/svg+xml",
    };
}

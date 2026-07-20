namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Builder-time configuration for request decompression, captured when
/// <c>UseRequestDecompression</c> is called. Its one knob is the decompressed-size guard that bounds
/// how many bytes a coded request body may inflate to before the request is rejected.
/// </summary>
/// <remarks>
/// The supported request codings are fixed &#8212; <c>gzip</c>, <c>br</c>, and <c>deflate</c>
/// (interpreted as the RFC 9110 &#167; 8.4.1.2 zlib format) &#8212; and a request naming any other
/// coding is answered with <c>415 Unsupported Media Type</c>. The size guard is the essential
/// setting: the transport's byte cap protects only the compressed wire bytes, so without a
/// decompressed cap a small coded body could inflate without bound (a zip bomb).
/// </remarks>
public sealed class RequestDecompressionOptions
{
    /// <summary>
    /// Gets or sets the maximum number of bytes a request body may decompress to. When a handler's
    /// read of the decompressed body would exceed this many bytes, the read fails and the request is
    /// answered with <c>413 Content Too Large</c> (when the response has not started). Defaults to
    /// <c>104857600</c> (100&#8239;MiB). The limit is applied to the fully decoded output, including
    /// across a multi-coding chain.
    /// </summary>
    public long MaxDecompressedSizeBytes { get; set; } = 100L * 1024 * 1024;
}

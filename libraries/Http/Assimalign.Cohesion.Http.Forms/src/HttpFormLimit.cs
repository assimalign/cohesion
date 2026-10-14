namespace Assimalign.Cohesion.Http;

/// <summary>
/// Identifies the configured form limit that a parser exceeded, independently of request input
/// or exception message text.
/// </summary>
public enum HttpFormLimit
{
    /// <summary>
    /// No limit was specified, as with the message-only
    /// <see cref="HttpFormLimitExceededException"/> constructor.
    /// Framework parsers always report a specific limit.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The number of urlencoded values exceeded <see cref="HttpFormOptions.ValueCountLimit"/>.
    /// </summary>
    ValueCount = 1,

    /// <summary>
    /// A urlencoded key exceeded <see cref="HttpFormOptions.KeyLengthLimit"/>.
    /// </summary>
    KeyLength = 2,

    /// <summary>
    /// A urlencoded value exceeded <see cref="HttpFormOptions.ValueLengthLimit"/>.
    /// </summary>
    ValueLength = 3,

    /// <summary>
    /// A multipart section body exceeded <see cref="HttpFormOptions.MultipartBodyLengthLimit"/>.
    /// </summary>
    MultipartBodyLength = 4,

    /// <summary>
    /// The number of multipart sections exceeded <see cref="HttpFormOptions.ValueCountLimit"/>.
    /// Each section counts, including files, repeated fields, and sections without a field name.
    /// </summary>
    MultipartSectionCount = 5,

    /// <summary>
    /// A multipart section's header count exceeded <see cref="HttpFormOptions.MultipartHeadersCountLimit"/>.
    /// </summary>
    MultipartHeadersCount = 6,

    /// <summary>
    /// A multipart header block or preamble exceeded its budget derived from
    /// <see cref="HttpFormOptions.MultipartHeadersLengthLimit"/>.
    /// </summary>
    MultipartHeadersLength = 7,

    /// <summary>
    /// A multipart boundary identifier exceeded <see cref="HttpFormOptions.MultipartBoundaryLengthLimit"/>.
    /// </summary>
    MultipartBoundaryLength = 8,
}

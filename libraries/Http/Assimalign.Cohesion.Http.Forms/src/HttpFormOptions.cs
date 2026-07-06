using System.IO;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-parse limits applied by <see cref="HttpFormFeature"/> when reading an
/// <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c>
/// request body. Every limit maps to a defence against unbounded memory or CPU
/// use by a hostile or malformed body; exceeding one throws an
/// <see cref="InvalidDataException"/> mid-parse.
/// </summary>
public class HttpFormOptions
{
    internal static readonly HttpFormOptions Default = new HttpFormOptions();

    /// <summary>
    /// Default value for <see cref="MemoryBufferThreshold"/>.
    /// Defaults to 65,536 bytes, which is approximately 64KB.
    /// </summary>
    public const int DefaultMemoryBufferThreshold = 1024 * 64;

    /// <summary>
    /// Default value for <see cref="MultipartBoundaryLengthLimit"/>.
    /// Defaults to 128 bytes.
    /// </summary>
    public const int DefaultMultipartBoundaryLengthLimit = 128;

    /// <summary>
    /// Default value for <see cref="MultipartBodyLengthLimit "/>.
    /// Defaults to 134,217,728 bytes, which is approximately 128MB.
    /// </summary>
    public const long DefaultMultipartBodyLengthLimit = 1024 * 1024 * 128;

    /// <summary>
    /// The number of bytes an individual <c>multipart/form-data</c> file section
    /// is buffered in memory before it spills to a temporary file on disk. This
    /// bounds peak memory for large uploads without forcing every upload through
    /// the file system.
    /// Defaults to 65,536 bytes, which is approximately 64KB.
    /// </summary>
    public int MemoryBufferThreshold { get; set; } = DefaultMemoryBufferThreshold;

    /// <summary>
    /// A limit for the number of form entries to allow.
    /// Forms that exceed this limit will throw an <see cref="InvalidDataException"/> when parsed.
    /// Defaults to 1024.
    /// </summary>
    public int ValueCountLimit { get; set; } = HttpFormReader.DefaultValueCountLimit;

    /// <summary>
    /// A limit on the length of individual keys. Forms containing keys that exceed this limit will
    /// throw an <see cref="InvalidDataException"/> when parsed.
    /// Defaults to 2,048 bytes, which is approximately 2KB.
    /// </summary>
    public int KeyLengthLimit { get; set; } = HttpFormReader.DefaultKeyLengthLimit;

    /// <summary>
    /// A limit on the length of individual form values. Forms containing values that exceed this
    /// limit will throw an <see cref="InvalidDataException"/> when parsed.
    /// Defaults to 4,194,304 bytes, which is approximately 4MB.
    /// </summary>
    public int ValueLengthLimit { get; set; } = HttpFormReader.DefaultValueLengthLimit;

    /// <summary>
    /// A limit for the length of the boundary identifier. Forms with boundaries that exceed this
    /// limit will throw an <see cref="InvalidDataException"/> when parsed.
    /// Defaults to 128 bytes.
    /// </summary>
    public int MultipartBoundaryLengthLimit { get; set; } = DefaultMultipartBoundaryLengthLimit;

    /// <summary>
    /// A limit for the number of headers to allow in each multipart section. Headers with the same name will
    /// be combined. Form sections that exceed this limit will throw an <see cref="InvalidDataException"/>
    /// when parsed.
    /// Defaults to 16.
    /// </summary>
    public int MultipartHeadersCountLimit { get; set; } = HttpMultipartFormReader.DefaultHeadersCountLimit;

    /// <summary>
    /// A limit for the total length of the header keys and values in each multipart section.
    /// Form sections that exceed this limit will throw an <see cref="InvalidDataException"/> when parsed.
    /// Defaults to 16,384 bytes, which is approximately 16KB.
    /// </summary>
    public int MultipartHeadersLengthLimit { get; set; } = HttpMultipartFormReader.DefaultHeadersLengthLimit;

    /// <summary>
    /// A limit for the length of each multipart body. Forms sections that exceed this limit will throw an
    /// <see cref="InvalidDataException"/> when parsed.
    /// Defaults to 134,217,728 bytes, which is approximately 128MB.
    /// </summary>
    public long MultipartBodyLengthLimit { get; set; } = DefaultMultipartBodyLengthLimit;
}

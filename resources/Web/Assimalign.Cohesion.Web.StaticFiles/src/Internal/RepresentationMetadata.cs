using System;
using System.Globalization;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Describes the selected representation a response serves (RFC 9110 &#167; 3.2): its media type,
/// length, validators, and the fields that ride on every outcome. It says nothing about where the
/// bytes come from — <see cref="RepresentationWriter"/> takes those separately — so a file in a mount
/// and a stream handed over by a handler are described the same way.
/// </summary>
internal readonly struct RepresentationMetadata
{
    /// <summary>
    /// Gets the <c>Content-Type</c> field value.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Gets the representation's length in bytes, or <see langword="null"/> when it is unknown. An
    /// unknown length is sent without <c>Content-Length</c> or <c>Accept-Ranges</c>, and a <c>Range</c>
    /// request is answered with the full representation.
    /// </summary>
    public long? Length { get; init; }

    /// <summary>
    /// Gets the representation's entity-tag, or <see langword="null"/> when it has none.
    /// </summary>
    public HttpEntityTag? ETag { get; init; }

    /// <summary>
    /// Gets the representation's last-modified time at HTTP-date (whole-second, UTC) precision, or
    /// <see langword="null"/> when it is unknown.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Gets the <c>Content-Encoding</c> of a precompressed representation, or <see langword="null"/>
    /// for the identity encoding.
    /// </summary>
    public string? ContentEncoding { get; init; }

    /// <summary>
    /// Gets the <c>Cache-Control</c> value emitted with the <c>200</c>, <c>206</c>, <c>304</c>, and
    /// <c>416</c> outcomes, or <see langword="null"/> to emit none.
    /// </summary>
    public string? CacheControl { get; init; }

    /// <summary>
    /// Gets a value indicating whether those outcomes carry <c>Vary: Accept-Encoding</c>.
    /// </summary>
    public bool VaryByAcceptEncoding { get; init; }

    /// <summary>
    /// Describes <paramref name="file"/>: its <c>Size</c> as the length, a strong entity-tag derived
    /// from <c>Size</c> and <c>UpdatedOn</c>, and <c>UpdatedOn</c> as the last-modified time.
    /// </summary>
    /// <param name="file">The file to describe.</param>
    /// <param name="contentType">The <c>Content-Type</c> to serve the file with.</param>
    /// <returns>The file's representation metadata.</returns>
    public static RepresentationMetadata ForFile(IFileSystemFile file, string contentType)
    {
        // Size is read first: the physical mount refreshes its cached file information on that read,
        // so UpdatedOn below reflects the same refresh.
        long length = file.Size;
        DateTimeOffset updatedOn = NormalizeTimestamp(file.UpdatedOn);

        return new RepresentationMetadata
        {
            ContentType = contentType,
            Length = length,
            // Strong validator from Size + UpdatedOn: hex ticks and hex length are valid etagc
            // characters, and a precompressed sibling naturally yields a different tag than the
            // identity file — distinct representations must not share a strong ETag. The tag keeps
            // full tick resolution for discrimination; Last-Modified is truncated to HTTP-date
            // precision for emission and comparison.
            ETag = HttpEntityTag.Strong(string.Create(
                CultureInfo.InvariantCulture,
                $"{updatedOn.UtcTicks:x}-{length:x}")),
            LastModified = TruncateToSeconds(updatedOn),
        };
    }

    /// <summary>
    /// Converts <paramref name="value"/> to UTC and drops its sub-second component: HTTP-date carries
    /// one-second resolution, so validators must compare at the precision they are emitted with.
    /// </summary>
    /// <param name="value">The timestamp to truncate.</param>
    /// <returns>The UTC timestamp at whole-second precision.</returns>
    public static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
        => new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static DateTimeOffset NormalizeTimestamp(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
        // File systems that don't stamp a kind report wall-clock UTC in this repo's mounts.
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };
}

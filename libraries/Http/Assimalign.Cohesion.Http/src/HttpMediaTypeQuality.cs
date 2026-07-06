using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single parsed <c>Accept</c> header entry: a media range paired with its RFC 9110
/// &#167; 12.4.2 quality weight.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpMediaTypeQuality : IEquatable<HttpMediaTypeQuality>
{
    /// <summary>
    /// Initializes a new Accept entry.
    /// </summary>
    /// <param name="mediaType">The media range.</param>
    /// <param name="quality">The quality weight for the range.</param>
    public HttpMediaTypeQuality(HttpMediaType mediaType, HttpQuality quality)
    {
        MediaType = mediaType;
        Quality = quality;
    }

    /// <summary>Gets the media range (which may contain wildcards).</summary>
    public HttpMediaType MediaType { get; }

    /// <summary>Gets the quality weight associated with the range.</summary>
    public HttpQuality Quality { get; }

    private string DebuggerDisplay => $"{MediaType}; q={Quality}";

    /// <inheritdoc />
    public bool Equals(HttpMediaTypeQuality other)
        => MediaType.Equals(other.MediaType) && Quality.Equals(other.Quality);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpMediaTypeQuality other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(MediaType, Quality);

    /// <inheritdoc />
    public override string ToString() => DebuggerDisplay;

    /// <summary>Determines whether two entries are equal.</summary>
    public static bool operator ==(HttpMediaTypeQuality left, HttpMediaTypeQuality right) => left.Equals(right);

    /// <summary>Determines whether two entries are not equal.</summary>
    public static bool operator !=(HttpMediaTypeQuality left, HttpMediaTypeQuality right) => !left.Equals(right);
}

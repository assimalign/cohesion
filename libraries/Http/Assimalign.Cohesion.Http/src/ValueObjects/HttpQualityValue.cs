using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single parsed entry from a token-valued preference header
/// (<c>Accept-Charset</c>, <c>Accept-Encoding</c>, <c>Accept-Language</c>): a value token
/// (which may be the wildcard <c>*</c>) paired with its RFC 9110 &#167; 12.4.2 quality weight.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpQualityValue : IEquatable<HttpQualityValue>
{
    /// <summary>
    /// Initializes a new weighted value.
    /// </summary>
    /// <param name="value">The value token (e.g. a content-coding, charset, or language range).</param>
    /// <param name="quality">The quality weight.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public HttpQualityValue(string value, HttpQuality quality)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
        Quality = quality;
    }

    /// <summary>Gets the value token (e.g. <c>gzip</c>, <c>utf-8</c>, <c>en-US</c>, or <c>*</c>).</summary>
    public string Value { get; }

    /// <summary>Gets the quality weight associated with the value.</summary>
    public HttpQuality Quality { get; }

    /// <summary>Gets a value indicating whether the value token is the wildcard <c>*</c>.</summary>
    public bool IsWildcard => Value == "*";

    private string DebuggerDisplay => $"{Value}; q={Quality}";

    /// <summary>
    /// Determines whether this entry's value matches <paramref name="candidate"/> (case-insensitive).
    /// </summary>
    /// <param name="candidate">The candidate value to test.</param>
    /// <returns><see langword="true"/> when the values match ignoring case.</returns>
    public bool Matches(string candidate)
        => string.Equals(Value, candidate, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool Equals(HttpQualityValue other)
        => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase) && Quality.Equals(other.Quality);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpQualityValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Value), Quality);

    /// <inheritdoc />
    public override string ToString() => DebuggerDisplay;

    /// <summary>Determines whether two entries are equal.</summary>
    public static bool operator ==(HttpQualityValue left, HttpQualityValue right) => left.Equals(right);

    /// <summary>Determines whether two entries are not equal.</summary>
    public static bool operator !=(HttpQualityValue left, HttpQualityValue right) => !left.Equals(right);
}

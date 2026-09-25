using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An unrecognized <c>Cache-Control</c> extension directive (RFC 9111 &#167; 5.2.3): a directive
/// name with an optional argument. The name is lower-cased; the argument, when present, is the
/// unescaped token or quoted-string value.
/// </summary>
public readonly struct HttpCacheControlExtension : IEquatable<HttpCacheControlExtension>
{
    /// <summary>
    /// Initializes a new extension directive.
    /// </summary>
    /// <param name="name">The directive name (lower-cased by convention).</param>
    /// <param name="value">The directive argument, or <see langword="null"/> when the directive has none.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public HttpCacheControlExtension(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Gets the directive name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the directive argument, or <see langword="null"/> when the directive is valueless.
    /// </summary>
    public string? Value { get; }

    /// <inheritdoc />
    public bool Equals(HttpCacheControlExtension other)
        => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpCacheControlExtension other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
            Value is null ? 0 : string.GetHashCode(Value, StringComparison.Ordinal));

    /// <inheritdoc />
    public override string ToString() => Value is null ? Name : $"{Name}={Value}";
}

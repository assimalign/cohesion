using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single media-type parameter (RFC 9110 &#167; 8.3.1) — a <c>name=value</c> pair such as
/// <c>charset=utf-8</c> or <c>boundary=abc</c>. Parameter names are case-insensitive; values
/// are compared case-insensitively so that negotiation treats, for example,
/// <c>charset=UTF-8</c> and <c>charset=utf-8</c> as equal.
/// </summary>
[DebuggerDisplay("{Name}={Value}")]
public readonly struct HttpMediaTypeParameter : IEquatable<HttpMediaTypeParameter>
{
    /// <summary>
    /// Initializes a new media-type parameter.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The parameter value (already unescaped from any quoted-string form).</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public HttpMediaTypeParameter(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Value = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the parameter name (e.g. <c>charset</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the parameter value (e.g. <c>utf-8</c>), with any quoted-string escaping already resolved.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Determines whether this parameter has the given name (case-insensitive).
    /// </summary>
    /// <param name="name">The name to test.</param>
    /// <returns><see langword="true"/> when the names match ignoring case.</returns>
    public bool HasName(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool Equals(HttpMediaTypeParameter other)
        => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpMediaTypeParameter other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Value));

    /// <inheritdoc />
    public override string ToString() => $"{Name}={Value}";

    /// <summary>Determines whether two parameters are equal.</summary>
    public static bool operator ==(HttpMediaTypeParameter left, HttpMediaTypeParameter right) => left.Equals(right);

    /// <summary>Determines whether two parameters are not equal.</summary>
    public static bool operator !=(HttpMediaTypeParameter left, HttpMediaTypeParameter right) => !left.Equals(right);
}

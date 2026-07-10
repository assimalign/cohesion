using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single RFC 7239 &#167; 4 <c>forwarded-pair</c> — a <c>token "=" value</c> pair inside a
/// <see cref="HttpForwardedElement"/>, such as <c>for=192.0.2.60</c>, <c>proto=https</c>, or an
/// extension parameter a proxy defines. Parameter names are case-insensitive and stored
/// lower-cased; the value is stored already unescaped from any quoted-string form.
/// </summary>
[DebuggerDisplay("{Name}={Value}")]
public readonly struct HttpForwardedParameter : IEquatable<HttpForwardedParameter>
{
    /// <summary>
    /// Initializes a new forwarded parameter.
    /// </summary>
    /// <param name="name">The parameter name (an RFC 9110 token, e.g. <c>for</c>).</param>
    /// <param name="value">The parameter value, already unescaped from any quoted-string form.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public HttpForwardedParameter(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Value = value ?? string.Empty;
    }

    /// <summary>Gets the parameter name (e.g. <c>for</c>, <c>proto</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the parameter value, with any quoted-string escaping already resolved.</summary>
    public string Value { get; }

    /// <summary>
    /// Determines whether this parameter has the given name (case-insensitive).
    /// </summary>
    /// <param name="name">The name to test.</param>
    /// <returns><see langword="true"/> when the names match ignoring case.</returns>
    public bool HasName(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool Equals(HttpForwardedParameter other)
        => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpForwardedParameter other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
            StringComparer.Ordinal.GetHashCode(Value));

    /// <inheritdoc />
    public override string ToString() => $"{Name}={Value}";

    /// <summary>Determines whether two parameters are equal.</summary>
    public static bool operator ==(HttpForwardedParameter left, HttpForwardedParameter right) => left.Equals(right);

    /// <summary>Determines whether two parameters are not equal.</summary>
    public static bool operator !=(HttpForwardedParameter left, HttpForwardedParameter right) => !left.Equals(right);
}

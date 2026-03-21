using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP host value.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHost : IEquatable<HttpHost>
{
    /// <summary>
    /// Gets an empty host value.
    /// </summary>
    public static HttpHost Empty { get; } = new(string.Empty);

    /// <summary>
    /// Initializes a new host value.
    /// </summary>
    /// <param name="value">The raw host value.</param>
    public HttpHost(string? value)
    {
        Value = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the raw host value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(HttpHost other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is HttpHost other && Equals(other);
    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static implicit operator HttpHost(string value) => new(value);
    public static implicit operator string(HttpHost host) => host.Value;
}

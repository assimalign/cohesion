using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP query-string key.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpQueryKey : IEquatable<HttpQueryKey>, IComparable<HttpQueryKey>
{
    /// <summary>
    /// Initializes a new query key.
    /// </summary>
    /// <param name="value">The raw query key.</param>
    public HttpQueryKey(string value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>
    /// Gets the raw query key.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(HttpQueryKey other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc />
    public int CompareTo(HttpQueryKey other) => StringComparer.OrdinalIgnoreCase.Compare(Value, other.Value);

    public override bool Equals(object? obj) => obj is HttpQueryKey key && Equals(key);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    public static implicit operator HttpQueryKey(string value) => new(value);
    public static implicit operator string(HttpQueryKey key) => key.Value;
    public static bool operator ==(HttpQueryKey left, HttpQueryKey right) => left.Equals(right);
    public static bool operator !=(HttpQueryKey left, HttpQueryKey right) => !left.Equals(right);
}

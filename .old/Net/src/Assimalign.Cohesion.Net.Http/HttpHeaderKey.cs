using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Http.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHeaderKey :
    IEquatable<HttpHeaderKey>,
    IEqualityComparer<HttpHeaderKey>,
    IComparable<HttpHeaderKey>
{
    private const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpHeaderKey(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            ThrowUtility.ThrowArgumentNullException(nameof(value));
        }
        this.Value = value;
    }

    /// <summary>
    /// The raw query key.
    /// </summary>
    public string Value { get; }

    #region Overloads
    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
    /// <inheritdoc />
    public override int GetHashCode()
    {
        return string.GetHashCode(Value, comparison);
    }
    /// <inheritdoc />
    public override bool Equals(object? instance)
    {
        if (instance is HttpHeaderKey key)
        {
            return Equals(key);
        }

        return false;
    }
    #endregion

    #region Explicit Implementations
    /// <inheritdoc />
    bool IEquatable<HttpHeaderKey>.Equals(HttpHeaderKey other)
    {
        return Value.Equals(other.Value, comparison);
    }

    /// <inheritdoc />
    int IComparable<HttpHeaderKey>.CompareTo(HttpHeaderKey other)
    {
        return string.Compare(Value, other.Value, comparison);
    }

    /// <inheritdoc />
    bool IEqualityComparer<HttpHeaderKey>.Equals(HttpHeaderKey left, HttpHeaderKey right)
    {
        return left.Equals(right);
    }

    /// <inheritdoc />
    int IEqualityComparer<HttpHeaderKey>.GetHashCode(HttpHeaderKey obj)
    {
        return obj.GetHashCode();
    }
    #endregion

    #region Operators
    public static implicit operator HttpHeaderKey(string key) => new HttpHeaderKey(key);

    public static implicit operator string(HttpHeaderKey key) => key.Value;
    public static bool operator ==(HttpHeaderKey left, HttpHeaderKey right) => left.Equals(right);
    public static bool operator !=(HttpHeaderKey left, HttpHeaderKey right) => !left.Equals(right);
    #endregion
}
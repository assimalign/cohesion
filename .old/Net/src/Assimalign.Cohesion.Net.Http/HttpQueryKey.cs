using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Http.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpQueryKey : 
    IEquatable<HttpQueryKey>,
    IEqualityComparer<HttpQueryKey>,
    IComparable<HttpQueryKey>
{
    private const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpQueryKey(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            ThrowUtility.ThrowArgumentNullException(nameof(value));
        }
        Value = value;
    }

    /// <summary>
    /// The raw query key.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(HttpQueryKey other)
    {
       return string.Equals(Value, other.Value, comparison);
    }

    /// <inheritdoc />
    public bool Equals(HttpQueryKey left, HttpQueryKey right)
    {
        return left.Equals(right);
    }

    /// <inheritdoc />
    public int GetHashCode(HttpQueryKey obj)
    {
        return obj.GetHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(HttpQueryKey other)
    {
        return string.Compare(Value, other.Value, comparison);
    }

    #region Overloads
    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is HttpQueryKey key)
        {
            return Equals(key);
        }
        return false;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return string.GetHashCode(Value, comparison);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
    #endregion

    #region Operators
    public static implicit operator HttpQueryKey(string value) => new HttpQueryKey(value);

    public static implicit operator string(HttpQueryKey key) => key.Value;
    public static bool operator ==(HttpQueryKey left, HttpQueryKey right) => left.Equals(right);
    public static bool operator !=(HttpQueryKey left, HttpQueryKey right) => !left.Equals(right);
    #endregion
}

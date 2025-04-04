using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpQueryKey : IEquatable<HttpQueryKey>, IComparable<HttpQueryKey>
{
    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpQueryKey(string value)
    {
        Value = ThrowHelper.ThrowIfNullOrEmpty(value);
    }

    /// <summary>
    /// The raw query key.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(HttpQueryKey other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(other);
    }

    /// <inheritdoc />
    public int CompareTo(HttpQueryKey other)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(this, other);
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
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator HttpQueryKey(string value)
    {
        return new HttpQueryKey(value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator string(HttpQueryKey key)
    {
        return key.Value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(HttpQueryKey left, HttpQueryKey right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(HttpQueryKey left, HttpQueryKey right)
    {
        return !left.Equals(right);
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHeaderKey : IEquatable<HttpHeaderKey>, IComparable<HttpHeaderKey>
{
    #region Constructor

    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpHeaderKey(string value)
    {
        Value = ThrowHelper.ThrowIfNullOrEmpty(value);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The raw query key.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    #endregion

    #region Methods 

    /// <inheritdoc />
    public bool Equals(HttpHeaderKey other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(this, other);
    }

    /// <inheritdoc />
    public int CompareTo(HttpHeaderKey other)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(this, other);
    }

    #endregion

    #region Overloads

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
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

    #region Operators

    public static implicit operator HttpHeaderKey(string key)
    {
        return new HttpHeaderKey(key);
    }

    public static implicit operator string(HttpHeaderKey key)
    {
        return key.Value;
    }

    public static bool operator ==(HttpHeaderKey left, HttpHeaderKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HttpHeaderKey left, HttpHeaderKey right)
    {
        return !left.Equals(right);
    }

    #endregion
}
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly partial struct HttpQueryValue : IEquatable<HttpQueryValue>
{
    #region Constructors

    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    public HttpQueryValue(string value)
    {
        this.Value = value;
    }

    #endregion

    #region Properties

    /// <summary>
    /// 
    /// </summary>
    public string Value { get; }

    #endregion

    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public short GetInt16()
    {
        return short.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public int GetInt32()
    {
        return int.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public long GetInt64()
    {
        return long.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public decimal GetDecimal()
    {
        return decimal.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public double GetDouble()
    {
        return double.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public float GetFloat()
    {
        return float.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public bool GetBoolean()
    {
        return bool.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public DateOnly GetDate()
    {
        return DateOnly.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public DateTime GetDateTime()
    {
        return DateTime.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public DateTimeOffset GetDateTimeOffset()
    {
        return DateTimeOffset.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public TimeOnly GetTime()
    {
        return TimeOnly.Parse(Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public TimeSpan GetTimeSpan()
    {
        return TimeSpan.Parse(this.Value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(HttpQueryValue other)
    {
        return Equals(this, other, StringComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Equals(HttpQueryValue other, StringComparison comparison)
    {
        return Equals(this, other, comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool Equals(HttpQueryValue left, HttpQueryValue right)
    {
        return Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public static bool Equals(HttpQueryValue left, HttpQueryValue right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Equals(left.Value, right.Value);
    }

    #endregion

    #region Overloads

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is null)
        {
            return false;
        }
        if (obj is not HttpQueryValue value)
        {
            return false;
        }
        return Equals(value);
    }

    public override string ToString()
    {
        return Value;
    }

    public override int GetHashCode()
    {
        return this.Value.ToLower().GetHashCode() ^ this.Value.ToLower().GetHashCode();
    }

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator HttpQueryValue(string value)
    {
        return new HttpQueryValue(value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(HttpQueryValue left, HttpQueryValue right)
    {
        return Equals(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(HttpQueryValue left, HttpQueryValue right)
    {
        return !Equals(left, right);
    }

    #endregion

    public static HttpQueryValue Empty => new HttpQueryValue("");

}

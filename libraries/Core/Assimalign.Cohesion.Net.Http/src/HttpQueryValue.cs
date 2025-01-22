using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Http;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly partial struct HttpQueryValue : 
    IEquatable<HttpQueryValue>, 
    IEqualityComparer<HttpQueryValue>
{
    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    public HttpQueryValue(string value)
    {
        this.Value = value;
    }

    /// <summary>
    /// 
    /// </summary>
    public string Value { get; }


    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public short GetInt16() => short.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public int GetInt32() => int.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public long GetInt64() => long.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public decimal GetDecimal() => decimal.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public double GetDouble() => double.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public float GetFloat() => float.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public bool GetBoolean() => bool.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public DateOnly GetDate() => DateOnly.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public DateTime GetDateTime() => DateTime.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public DateTimeOffset GetDateTimeOffset() => DateTimeOffset.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public TimeOnly GetTime() => TimeOnly.Parse(this.Value);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public TimeSpan GetTimeSpan() => TimeSpan.Parse(this.Value);

    public override string ToString()
    {
        return Value;
    }

    public bool TryGetInt16(out short value)
    {
        value = 0;

        if (short.TryParse(this.Value, out short v))
        {
            value = v;
            return true;
        }
        return false;
    }


    public override int GetHashCode()
    {
        return this.Value.ToLower().GetHashCode() ^ this.Value.ToLower().GetHashCode();
    }
    bool IEquatable<HttpQueryValue>.Equals(HttpQueryValue other) => this.Value.Equals(other.Value, StringComparison.InvariantCultureIgnoreCase);
    bool IEqualityComparer<HttpQueryValue>.Equals(HttpQueryValue left, HttpQueryValue right) => left.Equals(right);
    int IEqualityComparer<HttpQueryValue>.GetHashCode([DisallowNull] HttpQueryValue instance) => instance.GetHashCode();
    //public override string ToString() => string.Format("{0}={1}", this.Key, this.Value);

    public static implicit operator short(HttpQueryValue query) => query.GetInt16();
    public static implicit operator int(HttpQueryValue query) => query.GetInt32();
    public static implicit operator long(HttpQueryValue query) => query.GetInt64();
    public static implicit operator bool(HttpQueryValue query) => query.GetBoolean();
    public static implicit operator DateOnly(HttpQueryValue query) => query.GetDate();
    public static implicit operator DateTime(HttpQueryValue query) => query.GetDateTime();
    public static implicit operator DateTimeOffset(HttpQueryValue query) => query.GetDateTimeOffset();
    public static implicit operator TimeOnly(HttpQueryValue query) => query.GetTime();
    public static implicit operator TimeSpan(HttpQueryValue query) => query.GetTimeSpan();


    public static implicit operator HttpQueryValue(string value) => new HttpQueryValue(value);


    public static HttpQueryValue Empty => new HttpQueryValue("");


    public static bool operator ==(HttpQueryValue left, HttpQueryValue right)
    {
        return Equals(left, right);
    }


    public static bool operator !=(HttpQueryValue left, HttpQueryValue right)
    {
        return !Equals(left, right);
    }

    public static bool Equals(HttpQueryValue left, HttpQueryValue right)
    {
        return left.Value == right.Value;
    }
}

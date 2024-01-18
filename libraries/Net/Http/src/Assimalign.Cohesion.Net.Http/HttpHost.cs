using System;
using System.Net;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Assimalign.Cohesion.Net.Http;

[DebuggerDisplay("{Value}")]
public readonly struct HttpHost : IEquatable<HttpHost>
{
    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpHost(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentNullException(nameof(value));
        }
        Value = value;
    }




    public string Value { get; }
    //public int? Port { get; }
    public bool Equals(HttpHost other)
    {
        return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }
    public override bool Equals(object? obj)
    {
        if (obj is  HttpHost other)
        {
            return Equals(other);
        }
        return false;
    }
    public override string ToString()
    {
        return Value;
    }
    public override int GetHashCode()
    {
        return string.GetHashCode(Value, StringComparison.OrdinalIgnoreCase);
    }



    public static implicit operator HttpHost(string value) => new HttpHost(value);

    public static implicit operator string(HttpHost host) => host.Value;
}

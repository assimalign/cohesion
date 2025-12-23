using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace Assimalign.Cohesion.Http;

[DebuggerDisplay("{ToString()}")]
public sealed class HttpCookie
{
    private readonly string _name;
    private readonly string _value;

    public HttpCookie(string name) : this(name, string.Empty) { }

    public HttpCookie(string name, string value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        Name = name;
        Value = value;
    }

    /// <summary>
    /// The name of the cookie.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 
    /// </summary>
    public string Value { get; }


    /// <summary>
    /// 
    /// </summary>
    public bool HasValue
    {
        get { return Value == string.Empty; }
    }



    public override string ToString()
    {


        return string.Empty;
    }



   // public static implicit operator string

}

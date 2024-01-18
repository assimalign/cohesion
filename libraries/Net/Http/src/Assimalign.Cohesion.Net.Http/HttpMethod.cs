﻿using System;
using System.Linq;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Http.Internal;

public readonly struct HttpMethod
{
    public HttpMethod(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentNullException(nameof(value));
        }
        if (value.Any(x=> !char.IsLetterOrDigit(x)))
        {
            ThrowUtility.InvalidHttpMethod(value);
        }
        this.Value = value.ToUpper();
    }

    public string Value { get; }

    public override string ToString()
    {
        return $"Method: {Value}";
    }



    /// <summary>
    /// HTTP "CONNECT" method.
    /// </summary>
    public static readonly string Connect = "CONNECT";
    /// <summary>
    /// HTTP "DELETE" method.
    /// </summary>
    public static readonly string Delete = "DELETE";
    /// <summary>
    /// HTTP "GET" method.
    /// </summary>
    public static readonly string Get = "GET";
    /// <summary>
    /// HTTP "HEAD" method.
    /// </summary>
    public static readonly string Head = "HEAD";
    /// <summary>
    /// HTTP "OPTIONS" method.
    /// </summary>
    public static readonly string Options = "OPTIONS";
    /// <summary>
    /// HTTP "PATCH" method.
    /// </summary>
    public static readonly string Patch = "PATCH";
    /// <summary>
    /// HTTP "POST" method.
    /// </summary>
    public static readonly string Post = "POST";
    /// <summary>
    /// HTTP "PUT" method.
    /// </summary>
    public static readonly string Put = "PUT";
    /// <summary>
    /// HTTP "TRACE" method.
    /// </summary>
    public static readonly string Trace = "TRACE";

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is CONNECT.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is CONNECT; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsConnect(string method)
    {
        return Equals(Connect, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is DELETE.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is DELETE; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsDelete(string method)
    {
        return Equals(Delete, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is GET.
    /// </summary>
    /// <param name="method">The  HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is GET; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsGet(string method)
    {
        return Equals(Get, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is HEAD.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is HEAD; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsHead(string method)
    {
        return Equals(Head, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is OPTIONS.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is OPTIONS; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsOptions(string method)
    {
        return Equals(Options, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is PATCH.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is PATCH; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsPatch(string method)
    {
        return Equals(Patch, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is POST.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is POST; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsPost(string method)
    {
        return Equals(Post, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is PUT.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is PUT; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsPut(string method)
    {
        return Equals(Put, method);
    }

    /// <summary>
    /// Returns a value that indicates if the HTTP request method is TRACE.
    /// </summary>
    /// <param name="method">The HTTP request method.</param>
    /// <returns>
    /// <see langword="true" /> if the method is TRACE; otherwise, <see langword="false" />.
    /// </returns>
    public static bool IsTrace(string method)
    {
        return Equals(Trace, method);
    }

    /// <summary>
    ///  Returns the equivalent static instance, or the original instance if none match. 
    ///  This conversion is optional but allows for performance optimizations when comparing method values elsewhere.
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    public static string GetCanonicalizedValue(string method) => method switch
    {
        string _ when IsGet(method) => Get,
        string _ when IsPost(method) => Post,
        string _ when IsPut(method) => Put,
        string _ when IsDelete(method) => Delete,
        string _ when IsOptions(method) => Options,
        string _ when IsHead(method) => Head,
        string _ when IsPatch(method) => Patch,
        string _ when IsTrace(method) => Trace,
        string _ when IsConnect(method) => Connect,
        string _ => method
    };

    /// <summary>
    /// Returns a value that indicates if the HTTP methods are the same.
    /// </summary>
    /// <param name="methodA">The first HTTP request method to compare.</param>
    /// <param name="methodB">The second HTTP request method to compare.</param>
    /// <returns>
    /// <see langword="true" /> if the methods are the same; otherwise, <see langword="false" />.
    /// </returns>
    private static bool Equals(string methodA, string methodB)
    {
        return object.ReferenceEquals(methodA, methodB) || StringComparer.OrdinalIgnoreCase.Equals(methodA, methodB);
    }

    public static implicit operator HttpMethod(string method) => new HttpMethod(method);


    public static bool operator ==(HttpMethod left, HttpMethod right)
    {
        return Equals(left, right);
    }
    public static bool operator !=(HttpMethod left, HttpMethod right)
    {
        return !Equals(left, right);
    }


}

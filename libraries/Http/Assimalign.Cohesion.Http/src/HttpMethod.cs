using System;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("{Value}")]
public readonly struct HttpMethod : IEquatable<HttpMethod>
{
    const int _length = 16;

    #region Constructors

    /// <summary>
    /// The default constructor
    /// </summary>
    /// <param name="value"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpMethod(string value)
    {
        if (value.Length > _length)
        {
            ThrowHelper.ThrowArgumentException($"The method is too long. Must be under {_length} characters.");
        }

        ReadOnlySpan<char> source = ThrowHelper.ThrowIfNullOrEmpty(value);
        Span<char> destination = stackalloc char[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (!char.IsLetterOrDigit(c))
            {
                ThrowHelper.InvalidHttpMethod(value);
            }
            if (char.IsLower(c))
            {
                c = char.ToUpper(c);
            }
            destination[i] = c;
        }

        Value = destination.ToString();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The raw http value.
    /// </summary>
    public string? Value { get; } 

    /// <summary>
    /// HTTP "CONNECT" method.
    /// </summary>
    public static readonly HttpMethod Connect = "CONNECT";

    /// <summary>
    /// HTTP "DELETE" method.
    /// </summary>
    public static readonly HttpMethod Delete = "DELETE";

    /// <summary>
    /// HTTP "GET" method.
    /// </summary>
    public static readonly HttpMethod Get = "GET";

    /// <summary>
    /// HTTP "HEAD" method.
    /// </summary>
    public static readonly HttpMethod Head = "HEAD";

    /// <summary>
    /// HTTP "OPTIONS" method.
    /// </summary>
    public static readonly HttpMethod Options = "OPTIONS";

    /// <summary>
    /// HTTP "PATCH" method.
    /// </summary>
    public static readonly HttpMethod Patch = "PATCH";

    /// <summary>
    /// HTTP "POST" method.
    /// </summary>
    public static readonly HttpMethod Post = "POST";

    /// <summary>
    /// HTTP "PUT" method.
    /// </summary>
    public static readonly HttpMethod Put = "PUT";

    /// <summary>
    /// HTTP "TRACE" method.
    /// </summary>
    public static readonly HttpMethod Trace = "TRACE";

    #endregion

    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(HttpMethod other)
    {
        return Equals(this, other);
    }

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
    public static HttpMethod GetCanonicalizedValue(string method) => method switch
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
    private static bool Equals(string? methodA, string? methodB)
    {
        return object.ReferenceEquals(methodA, methodB) || StringComparer.OrdinalIgnoreCase.Equals(methodA, methodB);
    }

    #endregion

    #region Overloads

    /// <inheritdoc />
    public override string? ToString()
    {
        return Value;
    }

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is HttpMethod method)
        {
            return Equals(method);
        }

        return false;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return Value?.GetHashCode() ?? string.Empty.GetHashCode();
    }

    #endregion

    #region Operators

    /// <summary>
    /// Implicit conversion from string to HttpMethod.
    /// </summary>
    /// <param name="method"></param>
    public static implicit operator HttpMethod(string method)
    {
        return new HttpMethod(method);
    }

    /// <summary>
    /// Implicit conversion from HttpMethod to string.
    /// </summary>
    /// <param name="method"></param>
    public static implicit operator string?(HttpMethod method)
    {
        return method.Value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(HttpMethod left, HttpMethod right)
    {
        return Equals(left!, right!);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(HttpMethod left, HttpMethod right)
    {
        return !Equals(left!, right!);
    }

    #endregion
}

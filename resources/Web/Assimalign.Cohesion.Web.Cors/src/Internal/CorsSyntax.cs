using System;
using System.Buffers;

namespace Assimalign.Cohesion.Web.Cors.Internal;

/// <summary>
/// The RFC 9110 and Fetch syntax rules the policy model and the middleware share: method and field-name
/// tokens, Fetch's method normalization, and the CORS-safelisted methods.
/// </summary>
internal static class CorsSyntax
{
    /// <summary>
    /// The longest method token routing accepts (<c>HttpMethod</c>'s limit). A longer
    /// <c>Access-Control-Request-Method</c> is not a preflight to routing, so it is not one to CORS either.
    /// </summary>
    internal const int MaximumMethodLength = 32;

    // RFC 9110 §5.6.2: token = 1*tchar.
    private static readonly SearchValues<char> _tokenCharacters =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // The methods Fetch's "normalize a method" uppercases.
    private static readonly string[] _normalizedMethods = ["DELETE", "GET", "HEAD", "OPTIONS", "POST", "PUT"];

    /// <summary>
    /// Gets whether <paramref name="value"/> is an RFC 9110 token, the syntax of a method and of a field name.
    /// </summary>
    internal static bool IsToken(ReadOnlySpan<char> value) => !value.IsEmpty && !value.ContainsAnyExcept(_tokenCharacters);

    /// <summary>
    /// Applies Fetch's "normalize a method": <c>DELETE</c>, <c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c>,
    /// <c>POST</c> and <c>PUT</c> are uppercased when they match case-insensitively, because a browser
    /// always sends them uppercase. Every other method keeps its case, because methods are otherwise
    /// compared byte for byte.
    /// </summary>
    internal static string NormalizeMethod(string method)
    {
        foreach (string normalized in _normalizedMethods)
        {
            if (method.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return method;
    }

    /// <summary>
    /// Gets whether <paramref name="method"/> is a Fetch CORS-safelisted method: <c>GET</c>, <c>HEAD</c>
    /// or <c>POST</c>, compared byte for byte. A browser accepts a safelisted method whatever
    /// <c>Access-Control-Allow-Methods</c> lists, so a policy cannot restrict one through CORS.
    /// </summary>
    internal static bool IsSafelistedMethod(string method) => method is "GET" or "HEAD" or "POST";
}

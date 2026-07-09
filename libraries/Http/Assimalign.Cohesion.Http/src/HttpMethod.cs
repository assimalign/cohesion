using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP method token.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpMethod : IEquatable<HttpMethod>
{
    private const int MaximumLength = 32;
    private static readonly SearchValues<char> AllowedCharacters = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Initializes a new HTTP method.
    /// </summary>
    /// <param name="value">The method token.</param>
    public HttpMethod(string? value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIf(value.Length > MaximumLength, $"The method is too long. It must be {MaximumLength} characters or fewer.");
        ArgumentException.ThrowIf(value.AsSpan().ContainsAnyExcept(AllowedCharacters), $"The provided method is invalid: '{value}'.");


        Value = value.ToUpperInvariant();
    }

    /// <summary>
    /// Gets the raw HTTP method token.
    /// </summary>
    public string Value { get; }

    /// <summary>Gets the <c>CONNECT</c> method (RFC 9110 &#167; 9.3.6).</summary>
    public static HttpMethod Connect { get; } = new("CONNECT");

    /// <summary>Gets the <c>DELETE</c> method (RFC 9110 &#167; 9.3.5).</summary>
    public static HttpMethod Delete { get; } = new("DELETE");

    /// <summary>Gets the <c>GET</c> method (RFC 9110 &#167; 9.3.1).</summary>
    public static HttpMethod Get { get; } = new("GET");

    /// <summary>Gets the <c>HEAD</c> method (RFC 9110 &#167; 9.3.2).</summary>
    public static HttpMethod Head { get; } = new("HEAD");

    /// <summary>Gets the <c>OPTIONS</c> method (RFC 9110 &#167; 9.3.7).</summary>
    public static HttpMethod Options { get; } = new("OPTIONS");

    /// <summary>Gets the <c>PATCH</c> method (RFC 5789).</summary>
    public static HttpMethod Patch { get; } = new("PATCH");

    /// <summary>Gets the <c>POST</c> method (RFC 9110 &#167; 9.3.3).</summary>
    public static HttpMethod Post { get; } = new("POST");

    /// <summary>Gets the <c>PUT</c> method (RFC 9110 &#167; 9.3.4).</summary>
    public static HttpMethod Put { get; } = new("PUT");

    /// <summary>
    /// Gets the <c>QUERY</c> method (RFC 10008): a safe, idempotent method that carries the query
    /// in the request content, ending the POST-for-search workaround.
    /// </summary>
    /// <remarks>
    /// RFC 10008 &#167; 5.1 registers QUERY as both safe and idempotent, and its responses are
    /// cacheable with the request content forming part of the cache key (&#167; 2.7) — hence
    /// <see cref="IsSafe"/>, <see cref="IsIdempotent"/>, <see cref="IsCacheable"/>, and
    /// <see cref="CacheKeyIncludesContent"/> all report <see langword="true"/> for this method.
    /// </remarks>
    public static HttpMethod Query { get; } = new("QUERY");

    /// <summary>Gets the <c>TRACE</c> method (RFC 9110 &#167; 9.3.8).</summary>
    public static HttpMethod Trace { get; } = new("TRACE");

    /// <summary>
    /// Gets a value indicating whether this method is <em>safe</em> (RFC 9110 &#167; 9.2.1): it is
    /// essentially read-only, so automated agents may invoke it without concern for state change.
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> for GET, HEAD, OPTIONS, and TRACE (RFC 9110 &#167; 9.2.1) and for QUERY
    /// (RFC 10008 &#167; 5.1); <see langword="false"/> for PUT, DELETE, POST, PATCH, and CONNECT, and
    /// for any unrecognized extension method (its safety is unknown, so it is treated as unsafe).
    /// </remarks>
    public bool IsSafe => Value switch
    {
        "GET" or "HEAD" or "OPTIONS" or "TRACE" or "QUERY" => true,
        _ => false,
    };

    /// <summary>
    /// Gets a value indicating whether this method is <em>idempotent</em> (RFC 9110 &#167; 9.2.2): the
    /// intended effect of several identical requests is the same as that of a single request.
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> for every safe method (see <see cref="IsSafe"/>) plus PUT and DELETE
    /// (RFC 9110 &#167; 9.2.2), and for QUERY (RFC 10008 &#167; 5.1); <see langword="false"/> for POST,
    /// PATCH, and CONNECT, and for any unrecognized extension method.
    /// </remarks>
    public bool IsIdempotent => Value switch
    {
        "GET" or "HEAD" or "OPTIONS" or "TRACE" or "QUERY" or "PUT" or "DELETE" => true,
        _ => false,
    };

    /// <summary>
    /// Gets a value indicating whether responses to this method are, by definition, allowed to be
    /// stored for reuse by a cache (RFC 9110 &#167; 9.2.3).
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> for GET, HEAD, and POST (RFC 9110 &#167; 9.2.3) and for QUERY
    /// (RFC 10008 &#167; 2.7); <see langword="false"/> otherwise. This reports only that the method is
    /// defined as cacheable — an actual cache still applies the RFC 9111 storability rules
    /// (freshness, <c>Cache-Control</c>, and — for POST and QUERY — explicit freshness information)
    /// via <see cref="HttpCacheControl"/> and <see cref="HttpFreshness"/> before storing a response.
    /// </remarks>
    public bool IsCacheable => Value switch
    {
        "GET" or "HEAD" or "POST" or "QUERY" => true,
        _ => false,
    };

    /// <summary>
    /// Gets a value indicating whether a cache MUST incorporate the request content into the cache
    /// key for this method (RFC 10008 &#167; 2.7).
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> only for QUERY: because the query travels in the request content
    /// rather than the request target, two QUERY requests to the same URI with different content are
    /// distinct cache entries, so a cache MUST add the content (and its related metadata) to the
    /// cache key. Every other method keys solely on the request method and target URI, so this
    /// reports <see langword="false"/>.
    /// </remarks>
    public bool CacheKeyIncludesContent => Value switch
    {
        "QUERY" => true,
        _ => false,
    };

    /// <inheritdoc />
    public bool Equals(HttpMethod other)
    {
        return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a canonicalized method when one of the standard methods matches.
    /// </summary>
    /// <param name="method">The method to canonicalize.</param>
    /// <returns>A canonicalized method value.</returns>
    public static HttpMethod GetCanonicalizedValue(string method) => method.ToUpperInvariant() switch
    {
        "GET" => Get,
        "POST" => Post,
        "PUT" => Put,
        "DELETE" => Delete,
        "OPTIONS" => Options,
        "HEAD" => Head,
        "PATCH" => Patch,
        "TRACE" => Trace,
        "CONNECT" => Connect,
        "QUERY" => Query,
        _ => new HttpMethod(method),
    };

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is HttpMethod method && Equals(method);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public static implicit operator HttpMethod(string method) => new(method);
    public static implicit operator string(HttpMethod method) => method.Value;
    public static bool operator ==(HttpMethod left, HttpMethod right) => left.Equals(right);
    public static bool operator !=(HttpMethod left, HttpMethod right) => !left.Equals(right);
}

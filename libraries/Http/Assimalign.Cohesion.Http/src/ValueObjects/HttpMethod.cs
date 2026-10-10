using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP method token.
/// </summary>
/// <remarks>
/// <para>
/// Methods are case-sensitive (RFC 9110 &#167; 9.1). The token is kept exactly as it was given, and two
/// methods are equal only when their tokens match byte for byte. <c>get</c> is therefore an unknown
/// extension method, not <see cref="Get"/>: a server that read it as <c>GET</c> would apply semantics a
/// conformant intermediary in front of it does not, which lets a client step around a method-based
/// access rule, or desynchronize response framing with <c>head</c>.
/// </para>
/// <para>
/// <b>Breaking change.</b> In 10.0.0-preview.1 the constructor upper-cased the token and equality
/// ignored case. A method built from a string that is not upper case, including through the implicit
/// conversion from <see cref="string"/>, no longer equals the standard method it spells, and
/// <see cref="Value"/> keeps the case it was given.
/// </para>
/// </remarks>
[DebuggerDisplay("{Value}")]
public readonly struct HttpMethod : IEquatable<HttpMethod>
{
    private const int MaximumLength = 32;
    private static readonly SearchValues<char> _allowedCharacters = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Initializes a new HTTP method.
    /// </summary>
    /// <param name="value">The method token, kept as given: methods are case-sensitive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, longer than 32 characters, or not an RFC 9110 token.
    /// </exception>
    public HttpMethod(string? value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIf(value.Length > MaximumLength, $"The method is too long. It must be {MaximumLength} characters or fewer.");
        ArgumentException.ThrowIf(value.AsSpan().ContainsAnyExcept(_allowedCharacters), $"The provided method is invalid: '{value}'.");

        Value = value;
    }

    /// <summary>
    /// Gets the HTTP method token, in the case it was given.
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

    /// <summary>
    /// Compares two methods byte for byte (RFC 9110 &#167; 9.1): <c>get</c> does not equal <c>GET</c>.
    /// </summary>
    /// <param name="other">The method to compare with.</param>
    /// <returns><see langword="true"/> when both tokens are the same ordinal string.</returns>
    public bool Equals(HttpMethod other)
    {
        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the shared instance of a standard method when <paramref name="method"/> spells one exactly,
    /// otherwise a new method that keeps <paramref name="method"/> as it was sent.
    /// </summary>
    /// <remarks>
    /// The match is case-sensitive (RFC 9110 &#167; 9.1). <c>GET</c> returns <see cref="Get"/>, while
    /// <c>get</c> returns an unknown extension method whose <see cref="Value"/> is <c>get</c>. Every
    /// transport parses a request's method through this member.
    /// </remarks>
    /// <param name="method">The method token.</param>
    /// <returns>The standard method, or a new method for any other token.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="method"/> is not a valid method token.</exception>
    public static HttpMethod GetCanonicalizedValue(string method) => method switch
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
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>
    /// Converts a token to a method, keeping its case: <c>(HttpMethod)"get"</c> does not equal <see cref="Get"/>.
    /// </summary>
    /// <param name="method">The method token.</param>
    public static implicit operator HttpMethod(string method) => new(method);

    /// <summary>
    /// Returns the method's token.
    /// </summary>
    /// <param name="method">The method.</param>
    public static implicit operator string(HttpMethod method) => method.Value;

    /// <summary>
    /// Compares two methods byte for byte.
    /// </summary>
    /// <param name="left">The first method.</param>
    /// <param name="right">The second method.</param>
    public static bool operator ==(HttpMethod left, HttpMethod right) => left.Equals(right);

    /// <summary>
    /// Compares two methods byte for byte.
    /// </summary>
    /// <param name="left">The first method.</param>
    /// <param name="right">The second method.</param>
    public static bool operator !=(HttpMethod left, HttpMethod right) => !left.Equals(right);
}

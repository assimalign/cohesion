using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Cors.Internal;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Builds an immutable <see cref="CorsPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each value is validated when it is added, so the exception points at the offending call: an origin
/// must be an origin (<c>scheme://host[:port]</c>), and a method or header name must be an RFC 9110
/// token. Combinations are validated by <see cref="Build"/>.
/// </para>
/// <para>
/// A policy allows origins one of two ways: by listing them (<see cref="WithOrigins"/>) and, optionally, by
/// a predicate (<see cref="SetIsOriginAllowed"/>), in which case either may admit an origin; or by allowing
/// every origin (<see cref="AllowAnyOrigin"/>). Methods and request headers default to none beyond what
/// Fetch never asks a server to approve: a policy that does not call <see cref="WithMethods"/> or
/// <see cref="AllowAnyMethod"/> approves only the CORS-safelisted methods, and one that does not call
/// <see cref="WithHeaders"/> or <see cref="AllowAnyHeader"/> approves no request header that needs a
/// preflight.
/// </para>
/// </remarks>
public sealed class CorsPolicyBuilder
{
    private readonly List<string> _origins = new();
    private readonly List<string> _methods = new();
    private readonly List<string> _headers = new();
    private readonly List<string> _exposedHeaders = new();
    private Func<string, bool>? _originPredicate;
    private bool _allowAnyOrigin;
    private bool _allowAnyMethod;
    private bool _allowAnyHeader;
    private bool _supportsCredentials;
    private TimeSpan? _preflightMaxAge;

    /// <summary>
    /// Allows the listed origins. Each is normalized to the serialized form a browser sends in
    /// <c>Origin</c>: scheme and host lowercase, a default port (80 for <c>http</c>, 443 for <c>https</c>)
    /// omitted, an IPv6 address in its canonical form.
    /// </summary>
    /// <param name="origins">The origins, for example <c>https://app.example</c> or <c>http://localhost:5173</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="origins"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is not an origin: it is empty, <c>*</c> (call <see cref="AllowAnyOrigin"/>), the opaque origin
    /// <c>null</c> (call <see cref="SetIsOriginAllowed"/> to accept it deliberately), or it has a path
    /// (including a trailing <c>/</c>), query, fragment, user information, wildcard, invalid host or invalid
    /// port.
    /// </exception>
    public CorsPolicyBuilder WithOrigins(params string[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        foreach (string origin in origins)
        {
            _origins.Add(CorsOrigin.Normalize(origin, nameof(origins)));
        }

        return this;
    }

    /// <summary>
    /// Allows every origin. Responses then carry <c>Access-Control-Allow-Origin: *</c>, which cannot be
    /// combined with credentials.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public CorsPolicyBuilder AllowAnyOrigin()
    {
        _allowAnyOrigin = true;
        return this;
    }

    /// <summary>
    /// Sets a predicate that decides whether an origin not listed through <see cref="WithOrigins"/> is
    /// allowed. Replaces any predicate set before.
    /// </summary>
    /// <remarks>
    /// The predicate receives the request's origin exactly as the browser sent it, scheme included (for
    /// example <c>https://tenant.app.example</c>), and is consulted only when that value is a well-formed
    /// serialized origin or the opaque origin <c>null</c>. Check the scheme as well as the host: an
    /// <c>http</c> origin can be impersonated on a hostile network. A predicate that accepts every origin
    /// combined with <see cref="AllowCredentials"/> lets any site read responses with the user's credentials.
    /// Keep the predicate fast, free of side effects, and AOT-safe (no reflection).
    /// </remarks>
    /// <param name="predicate">Returns <see langword="true"/> to allow the origin.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public CorsPolicyBuilder SetIsOriginAllowed(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        _originPredicate = predicate;
        return this;
    }

    /// <summary>
    /// Allows the listed methods in a preflight. The CORS-safelisted methods (<c>GET</c>, <c>HEAD</c>,
    /// <c>POST</c>) are always allowed and need not be listed.
    /// </summary>
    /// <remarks>
    /// Methods compare byte for byte, as Fetch compares them. A browser uppercases <c>DELETE</c>, <c>GET</c>,
    /// <c>HEAD</c>, <c>OPTIONS</c>, <c>POST</c> and <c>PUT</c> before sending them, so those are uppercased
    /// here too; any other method keeps its case, so <c>PATCH</c> allows <c>fetch(url, { method: 'PATCH' })</c>
    /// but not <c>{ method: 'patch' }</c>.
    /// </remarks>
    /// <param name="methods">The methods, for example <c>PUT</c> and <c>DELETE</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="methods"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is not a method token, or is <c>*</c> (call <see cref="AllowAnyMethod"/>).
    /// </exception>
    public CorsPolicyBuilder WithMethods(params string[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        foreach (string method in methods)
        {
            ValidateToken(method, nameof(methods), "method", nameof(AllowAnyMethod));
            _methods.Add(CorsSyntax.NormalizeMethod(method));
        }

        return this;
    }

    /// <summary>
    /// Allows every method in a preflight.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public CorsPolicyBuilder AllowAnyMethod()
    {
        _allowAnyMethod = true;
        return this;
    }

    /// <summary>
    /// Allows the listed request headers in a preflight. Names compare case-insensitively.
    /// </summary>
    /// <remarks>
    /// A browser lists in <c>Access-Control-Request-Headers</c> only the headers that are not
    /// CORS-safelisted, and every listed header needs approval. That includes <c>Content-Type</c> with a
    /// value other than a form or plain-text media type, so a JSON API lists <c>Content-Type</c> here.
    /// </remarks>
    /// <param name="headers">The header names, for example <c>Content-Type</c> and <c>Authorization</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is not a header name, or is <c>*</c> (call <see cref="AllowAnyHeader"/>).
    /// </exception>
    public CorsPolicyBuilder WithHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (string header in headers)
        {
            ValidateToken(header, nameof(headers), "header name", nameof(AllowAnyHeader));
            _headers.Add(header);
        }

        return this;
    }

    /// <summary>
    /// Allows every request header in a preflight.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public CorsPolicyBuilder AllowAnyHeader()
    {
        _allowAnyHeader = true;
        return this;
    }

    /// <summary>
    /// Exposes the listed response headers to the calling script, beyond the CORS-safelisted response
    /// headers a browser always exposes.
    /// </summary>
    /// <param name="headers">The header names, for example <c>ETag</c> or <c>Location</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry is not a header name, or is <c>*</c>.</exception>
    public CorsPolicyBuilder WithExposedHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (string header in headers)
        {
            ValidateToken(header, nameof(headers), "header name", wildcardAlternative: null);
            _exposedHeaders.Add(header);
        }

        return this;
    }

    /// <summary>
    /// Shares responses with requests that carry credentials (cookies, HTTP authentication, client
    /// certificates): responses carry <c>Access-Control-Allow-Credentials: true</c>, and the allowed origin
    /// is always echoed rather than <c>*</c>.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public CorsPolicyBuilder AllowCredentials()
    {
        _supportsCredentials = true;
        return this;
    }

    /// <summary>
    /// Sets how long a browser may cache a preflight answer (<c>Access-Control-Max-Age</c>). Fractional
    /// seconds are truncated; browsers cap the value (Chromium at two hours, Firefox at a day).
    /// </summary>
    /// <param name="maxAge">The cache lifetime; zero disables caching.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAge"/> is negative.</exception>
    public CorsPolicyBuilder SetPreflightMaxAge(TimeSpan maxAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);

        _preflightMaxAge = maxAge;
        return this;
    }

    /// <summary>
    /// Builds the immutable policy. The builder may be changed and built again afterwards; policies built
    /// earlier are unaffected.
    /// </summary>
    /// <returns>The policy.</returns>
    /// <exception cref="InvalidOperationException">
    /// The policy allows no origin; it combines <see cref="AllowAnyOrigin"/> with a listed origin, an origin
    /// predicate, or <see cref="AllowCredentials"/>; or it combines <see cref="AllowAnyMethod"/> with
    /// <see cref="WithMethods"/>, or <see cref="AllowAnyHeader"/> with <see cref="WithHeaders"/>.
    /// </exception>
    public CorsPolicy Build()
    {
        if (_allowAnyOrigin)
        {
            if (_origins.Count > 0 || _originPredicate is not null)
            {
                throw new InvalidOperationException(
                    "AllowAnyOrigin() cannot be combined with WithOrigins or SetIsOriginAllowed: a policy that " +
                    "allows every origin has no origin to restrict.");
            }

            if (_supportsCredentials)
            {
                throw new InvalidOperationException(
                    "AllowAnyOrigin() cannot be combined with AllowCredentials(). The Fetch standard forbids " +
                    "'Access-Control-Allow-Origin: *' on a response to a credentialed request, and echoing every " +
                    "origin instead would let any site read responses with the user's credentials. List the " +
                    "trusted origins with WithOrigins, or decide per origin with SetIsOriginAllowed.");
            }
        }
        else if (_origins.Count == 0 && _originPredicate is null)
        {
            throw new InvalidOperationException(
                "A CORS policy must allow at least one origin. Call WithOrigins, SetIsOriginAllowed or AllowAnyOrigin().");
        }

        if (_allowAnyMethod && _methods.Count > 0)
        {
            throw new InvalidOperationException(
                "AllowAnyMethod() cannot be combined with WithMethods: a policy that allows every method has no method to list.");
        }

        if (_allowAnyHeader && _headers.Count > 0)
        {
            throw new InvalidOperationException(
                "AllowAnyHeader() cannot be combined with WithHeaders: a policy that allows every header has no header to list.");
        }

        return new CorsPolicy(
            _allowAnyOrigin,
            Distinct(_origins, StringComparer.Ordinal),
            _originPredicate,
            _allowAnyMethod,
            Distinct(_methods, StringComparer.Ordinal),
            _allowAnyHeader,
            Distinct(_headers, StringComparer.OrdinalIgnoreCase),
            Distinct(_exposedHeaders, StringComparer.OrdinalIgnoreCase),
            _supportsCredentials,
            _preflightMaxAge);
    }

    private static void ValidateToken(string value, string parameterName, string kind, string? wildcardAlternative)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"A {kind} must not be null or empty.", parameterName);
        }

        if (value == "*")
        {
            throw new ArgumentException(
                wildcardAlternative is null
                    ? $"'*' is not a {kind}. List the names explicitly."
                    : $"'*' is not a {kind}. Call {wildcardAlternative}() to allow every one.",
                parameterName);
        }

        if (!CorsSyntax.IsToken(value))
        {
            throw new ArgumentException($"'{value}' is not a {kind}: it must be an RFC 9110 token.", parameterName);
        }
    }

    // First occurrence wins, so the rendered header lists keep the order the policy was written in.
    private static string[] Distinct(List<string> values, StringComparer comparer)
    {
        if (values.Count == 0)
        {
            return [];
        }

        HashSet<string> seen = new(values.Count, comparer);
        List<string> distinct = new(values.Count);

        foreach (string value in values)
        {
            if (seen.Add(value))
            {
                distinct.Add(value);
            }
        }

        return distinct.ToArray();
    }
}

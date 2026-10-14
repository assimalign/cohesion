using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Web.Cors.Internal;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// An immutable CORS policy: which origins may read responses, which methods and request headers a
/// preflight may approve, which response headers the browser exposes to script, whether credentials are
/// allowed, and how long a browser may cache a preflight. Build one with <see cref="CorsPolicyBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// A policy is validated when it is built, so a misconfiguration fails at startup instead of at the
/// first cross-origin request: allowed origins are serialized origins, a policy that allows any origin
/// cannot allow credentials, and an explicit list cannot be combined with its "any" counterpart.
/// </para>
/// <para>
/// Matching follows the Fetch standard. Origins compare exactly against their serialized form. Methods
/// compare byte for byte, and the CORS-safelisted methods (<c>GET</c>, <c>HEAD</c>, <c>POST</c>) are always
/// allowed, because a browser accepts them whatever the response lists. Request-header names compare
/// case-insensitively.
/// </para>
/// <para>
/// A policy is used three ways: as the default policy (<see cref="CorsOptions.AddDefaultPolicy(CorsPolicy)"/>),
/// as a named policy (<see cref="CorsOptions.AddPolicy(string, CorsPolicy)"/>) that endpoints reference, or
/// attached inline to an endpoint through <see cref="CorsMetadata"/>.
/// </para>
/// </remarks>
public sealed class CorsPolicy
{
    private readonly FrozenSet<string> _origins;
    private readonly Func<string, bool>? _originPredicate;
    private readonly FrozenSet<string> _methods;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _headers;

    internal CorsPolicy(
        bool allowsAnyOrigin,
        string[] origins,
        Func<string, bool>? originPredicate,
        bool allowsAnyMethod,
        string[] methods,
        bool allowsAnyHeader,
        string[] headers,
        string[] exposedHeaders,
        bool supportsCredentials,
        TimeSpan? preflightMaxAge)
    {
        AllowsAnyOrigin = allowsAnyOrigin;
        Origins = Array.AsReadOnly(origins);
        _origins = origins.ToFrozenSet(StringComparer.Ordinal);
        _originPredicate = originPredicate;

        AllowsAnyMethod = allowsAnyMethod;
        Methods = Array.AsReadOnly(methods);
        _methods = methods.ToFrozenSet(StringComparer.Ordinal);

        AllowsAnyHeader = allowsAnyHeader;
        Headers = Array.AsReadOnly(headers);
        _headers = headers.ToFrozenSet(StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

        ExposedHeaders = Array.AsReadOnly(exposedHeaders);
        SupportsCredentials = supportsCredentials;
        PreflightMaxAge = preflightMaxAge;

        // The header values are fixed per policy, so they are rendered once here and every response
        // reuses them.
        AllowMethodsValue = methods.Length == 0 ? null : string.Join(", ", methods);
        AllowHeadersValue = headers.Length == 0 ? null : string.Join(", ", headers);
        ExposeHeadersValue = exposedHeaders.Length == 0 ? null : string.Join(", ", exposedHeaders);
        MaxAgeValue = preflightMaxAge is { } maxAge
            ? ((long)Math.Floor(maxAge.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Gets whether every origin may read responses. The response then carries
    /// <c>Access-Control-Allow-Origin: *</c>, which does not depend on the request, and never credentials.
    /// </summary>
    public bool AllowsAnyOrigin { get; }

    /// <summary>
    /// Gets the allowed origins, in their serialized form (<c>scheme://host[:port]</c>, lowercase, default
    /// port omitted). Empty when the policy allows any origin, or decides only through a predicate.
    /// </summary>
    public IReadOnlyList<string> Origins { get; }

    /// <summary>
    /// Gets whether every method may be approved by a preflight. A preflight answer then names the requested
    /// method itself, which is valid whether or not the request carries credentials.
    /// </summary>
    public bool AllowsAnyMethod { get; }

    /// <summary>
    /// Gets the methods a preflight may approve besides the CORS-safelisted <c>GET</c>, <c>HEAD</c> and
    /// <c>POST</c>, which are always allowed. Compared byte for byte.
    /// </summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>
    /// Gets whether every request header may be approved by a preflight. A preflight answer then names the
    /// requested headers themselves, so <c>Authorization</c>, which a <c>*</c> answer never covers, is
    /// approved too.
    /// </summary>
    public bool AllowsAnyHeader { get; }

    /// <summary>
    /// Gets the request headers a preflight may approve. Compared case-insensitively.
    /// </summary>
    public IReadOnlyList<string> Headers { get; }

    /// <summary>
    /// Gets the response headers, beyond the CORS-safelisted response headers, that the browser exposes to
    /// the calling script (<c>Access-Control-Expose-Headers</c>).
    /// </summary>
    public IReadOnlyList<string> ExposedHeaders { get; }

    /// <summary>
    /// Gets whether responses may be shared with requests that carry credentials (cookies, HTTP
    /// authentication, client certificates). The allowed origin is then always echoed, never <c>*</c>.
    /// </summary>
    public bool SupportsCredentials { get; }

    /// <summary>
    /// Gets how long a browser may cache a preflight answer (<c>Access-Control-Max-Age</c>, in whole
    /// seconds), or <see langword="null"/> to leave it to the browser's default of five seconds.
    /// </summary>
    public TimeSpan? PreflightMaxAge { get; }

    // Pre-rendered header values; null when the header is omitted.
    internal string? AllowMethodsValue { get; }

    internal string? AllowHeadersValue { get; }

    internal string? ExposeHeadersValue { get; }

    internal string? MaxAgeValue { get; }

    /// <summary>
    /// Gets whether the CORS response headers depend on the request's origin, so caches must key the
    /// response on it (<c>Vary: Origin</c>). Only a policy that allows any origin answers every origin alike.
    /// </summary>
    internal bool VariesByOrigin => !AllowsAnyOrigin;

    /// <summary>
    /// Gets whether the policy allows <paramref name="origin"/>.
    /// </summary>
    /// <param name="origin">
    /// A request origin as the browser sends it in the <c>Origin</c> header: a serialized origin, or
    /// <c>null</c> for an opaque one.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the policy allows any origin, lists <paramref name="origin"/>, or its
    /// predicate accepts it; otherwise <see langword="false"/>. The predicate is consulted only for a
    /// well-formed serialized origin (or <c>null</c>), so it never sees a value a browser would not send.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="origin"/> is <see langword="null"/>.</exception>
    public bool IsOriginAllowed(string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        return AllowsOrigin(origin);
    }

    internal bool AllowsOrigin(string origin)
    {
        if (AllowsAnyOrigin || _origins.Contains(origin))
        {
            return true;
        }

        return _originPredicate is { } predicate && CorsOrigin.IsSerialized(origin) && predicate(origin);
    }

    // Fetch's method check, mirrored: a listed method, a CORS-safelisted method, or any method.
    internal bool AllowsMethod(string method)
        => AllowsAnyMethod || CorsSyntax.IsSafelistedMethod(method) || _methods.Contains(method);

    internal bool AllowsHeader(ReadOnlySpan<char> name) => AllowsAnyHeader || _headers.Contains(name);
}

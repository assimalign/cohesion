using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Classification rules for HTTP field (header/trailer) names that higher
/// layers — transports, clients, servers, and the cross-version normalization
/// layer — share so they reason about repeated fields, combining, ordering,
/// connection-specific fields, and trailer eligibility consistently rather than
/// re-deriving the rules per protocol version.
/// </summary>
/// <remarks>
/// <para>
/// The rules are version-neutral statements of RFC 9110 field semantics. How a
/// given protocol version <em>applies</em> them (HTTP/2 and HTTP/3 reject
/// connection-specific fields outright; HTTP/1.1 strips them when forwarding)
/// is the concern of the per-version transport and the normalization layer; this
/// type is the single source of truth for the classification itself.
/// </para>
/// </remarks>
public static class HttpFieldRules
{
    // RFC 9110 §7.6.1 — connection-specific header fields. They apply only to
    // a single transport-level connection and MUST NOT be forwarded. RFC 9113
    // §8.2.2 / RFC 9114 §4.2 make their presence in HTTP/2 and HTTP/3 a
    // malformed message (with the narrow TE: trailers exception, handled by
    // ProhibitsInHttp2Or3 below).
    private static readonly HashSet<string> ConnectionSpecific = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "Upgrade",
    };

    // Singleton fields: a recipient that receives more than one MUST treat the
    // message as malformed (or use only the first), because combining them
    // changes meaning. This is the curated set of well-known singletons from
    // RFC 9110 and the field-specific definitions.
    private static readonly HashSet<string> Singletons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Content-Type",
        "Content-Range",
        "Content-Location",
        "Host",
        "Date",
        "Location",
        "Retry-After",
        "ETag",
        "Last-Modified",
        "Expires",
        "Age",
        "Authorization",
        "Proxy-Authorization",
        "From",
        "Referer",
        "User-Agent",
        "Max-Forwards",
        "Server",
    };

    // RFC 9110 §6.5.1 — fields that MUST NOT be sent in the trailer section.
    // Grouped by the categories the RFC enumerates: message framing, routing,
    // request modifiers, authentication, and content-processing controls. The
    // "Trailer" field and "Set-Cookie" are also excluded.
    private static readonly HashSet<string> TrailerProhibited = new(StringComparer.OrdinalIgnoreCase)
    {
        // Message framing.
        "Transfer-Encoding",
        "Content-Length",
        // Routing.
        "Host",
        // Request modifiers (controls / conditionals).
        "Cache-Control",
        "Expect",
        "Max-Forwards",
        "Pragma",
        "Range",
        "TE",
        // Authentication.
        "Authorization",
        "Proxy-Authorization",
        "WWW-Authenticate",
        "Proxy-Authenticate",
        // Response control data.
        "Age",
        "Expires",
        "Date",
        "Location",
        "Retry-After",
        "Vary",
        "Warning",
        // Content processing.
        "Content-Encoding",
        "Content-Type",
        "Content-Range",
        // The trailer-declaration field and connection management cannot recurse.
        "Trailer",
        "Connection",
        // Cookie state must not arrive as a trailer.
        "Set-Cookie",
        "Cookie",
    };

    /// <summary>
    /// Determines whether <paramref name="key"/> is a connection-specific field
    /// (RFC 9110 §7.6.1) — one that applies only to a single connection and
    /// MUST NOT be projected across a version boundary. HTTP/2 and HTTP/3 treat
    /// these as malformed if present.
    /// </summary>
    /// <param name="key">The field name to classify.</param>
    /// <returns><see langword="true"/> when the field is connection-specific.</returns>
    public static bool IsConnectionSpecific(HttpHeaderKey key)
    {
        return !key.IsEmpty && ConnectionSpecific.Contains(key.Value);
    }

    /// <summary>
    /// Determines whether <paramref name="key"/> is a singleton field that must
    /// not be repeated or list-combined, because combining instances would
    /// change the message's meaning (RFC 9110 §5.3 and field definitions).
    /// </summary>
    /// <param name="key">The field name to classify.</param>
    /// <returns><see langword="true"/> when the field must appear at most once.</returns>
    public static bool IsSingleton(HttpHeaderKey key)
    {
        return !key.IsEmpty && Singletons.Contains(key.Value);
    }

    /// <summary>
    /// Determines whether <paramref name="key"/> is <c>Set-Cookie</c>, which is
    /// the canonical field that MUST NOT be folded into a single comma-separated
    /// line: each cookie occupies its own field line (RFC 9110 §5.3 note;
    /// RFC 6265 §3). HTTP/2 and HTTP/3 likewise keep each <c>Set-Cookie</c> as a
    /// distinct field.
    /// </summary>
    /// <param name="key">The field name to classify.</param>
    /// <returns><see langword="true"/> when the field is <c>Set-Cookie</c>.</returns>
    public static bool IsSetCookie(HttpHeaderKey key)
    {
        return !key.IsEmpty && string.Equals(key.Value, "Set-Cookie", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether repeated instances of <paramref name="key"/> must be
    /// preserved as separate field lines rather than combined into one
    /// comma-separated value. Today this is <c>Set-Cookie</c> (per
    /// <see cref="IsSetCookie"/>); the method exists so callers express intent
    /// ("does this field combine?") without hard-coding the exception.
    /// </summary>
    /// <param name="key">The field name to classify.</param>
    /// <returns><see langword="true"/> when repeated instances must stay distinct.</returns>
    public static bool ProhibitsCombining(HttpHeaderKey key)
    {
        return IsSetCookie(key);
    }

    /// <summary>
    /// Determines whether <paramref name="key"/> is prohibited from appearing in
    /// the trailer section (RFC 9110 §6.5.1). Message-framing, routing, request
    /// modifier, authentication, and content-processing fields, plus the
    /// <c>Trailer</c> declaration field itself, must never be sent as trailers.
    /// </summary>
    /// <param name="key">The field name to classify.</param>
    /// <returns><see langword="true"/> when the field must not be used as a trailer.</returns>
    public static bool IsProhibitedInTrailers(HttpHeaderKey key)
    {
        return !key.IsEmpty && TrailerProhibited.Contains(key.Value);
    }
}

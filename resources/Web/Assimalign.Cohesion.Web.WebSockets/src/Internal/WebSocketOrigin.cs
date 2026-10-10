using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

/// <summary>
/// Origin parsing for the cross-site WebSocket hijacking defense. Every origin the policy compares
/// — the handshake's <c>Origin</c>, the request's own origin, and the configured allowed origins —
/// is reduced to one serialized form, <c>scheme://host[:port]</c>, with the scheme and host in lower
/// case, the scheme's default port dropped, and an IPv6 address in canonical form. Two origins are
/// the same origin when their serialized forms are equal, ordinally.
/// </summary>
/// <remarks>
/// Anything that is not part of an origin is rejected rather than stripped: a path (even a trailing
/// <c>/</c>), a query, a fragment, user information, a wildcard, whitespace and non-ASCII
/// characters. A browser sends an internationalized host in its ASCII (punycode) form.
/// </remarks>
internal static class WebSocketOrigin
{
    /// <summary>The serialization of an opaque origin, which a sandboxed page or a local file sends.</summary>
    public const string Opaque = "null";

    // RFC 3986 §3.1: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    private static readonly SearchValues<char> _schemeCharacters =
        SearchValues.Create("+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // Host-name characters an origin carries: letters, digits, '-', '.', '_'.
    private static readonly SearchValues<char> _hostCharacters =
        SearchValues.Create("-._0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Validates a configured origin and returns its serialized form.
    /// </summary>
    /// <param name="origin">The configured origin.</param>
    /// <returns>The serialized origin.</returns>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not an origin.</exception>
    public static string NormalizeConfigured(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            throw new ArgumentException("An allowed origin must not be null or empty.", nameof(WebSocketOptions.AllowedOrigins));
        }

        if (origin == "*")
        {
            throw new ArgumentException(
                "'*' is not an origin. Set AllowAnyOrigin to allow every origin.",
                nameof(WebSocketOptions.AllowedOrigins));
        }

        if (string.Equals(origin, Opaque, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The 'null' origin cannot be allowed by value: sandboxed pages, local files and cross-origin redirects " +
                "all send it, so allowing it would allow all of them. Set AllowAnyOrigin to accept it deliberately.",
                nameof(WebSocketOptions.AllowedOrigins));
        }

        if (!TryNormalize(origin, out string? serialized))
        {
            throw new ArgumentException(
                $"'{origin}' is not an origin: expected scheme://host[:port], for example https://app.example, " +
                "with no path (not even a trailing '/'), query, fragment, user information or wildcard.",
                nameof(WebSocketOptions.AllowedOrigins));
        }

        return serialized;
    }

    /// <summary>
    /// Computes the request's own origin from its effective, proxy-resolved scheme and host.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="origin">The serialized origin.</param>
    /// <returns><see langword="false"/> when the scheme is unknown or the host is not a valid authority.</returns>
    public static bool TryGetRequestOrigin(IHttpContext context, [NotNullWhen(true)] out string? origin)
    {
        origin = null;

        string? scheme = context.EffectiveScheme switch
        {
            HttpScheme.Http => "http",
            HttpScheme.Https => "https",
            _ => null,
        };

        HttpHost host = context.EffectiveHost;

        return scheme is not null && !host.IsEmpty && TryNormalize(scheme + "://" + host.Value, out origin);
    }

    /// <summary>
    /// Parses <paramref name="value"/> as an origin and returns its serialized form.
    /// </summary>
    /// <param name="value">The origin text.</param>
    /// <param name="serialized">The serialized origin.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> is not an origin, including the opaque origin.</returns>
    public static bool TryNormalize(string value, [NotNullWhen(true)] out string? serialized)
    {
        serialized = null;

        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> scheme = value.AsSpan(0, schemeEnd);
        if (!char.IsAsciiLetter(scheme[0]) || scheme.ContainsAnyExcept(_schemeCharacters))
        {
            return false;
        }

        ReadOnlySpan<char> authority = value.AsSpan(schemeEnd + 3);
        if (authority.IsEmpty)
        {
            return false;
        }

        string host;
        bool hasPort;
        ReadOnlySpan<char> port;

        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            ReadOnlySpan<char> literal = authority[1..close];

            // A zone identifier never appears in an origin.
            if (literal.Contains('%')
                || !IPAddress.TryParse(literal, out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            ReadOnlySpan<char> rest = authority[(close + 1)..];
            if (!rest.IsEmpty && rest[0] != ':')
            {
                return false;
            }

            // The BCL's canonical form (RFC 5952: lower case, the longest zero run compressed), the
            // same on every side of the comparison.
            host = "[" + address.ToString() + "]";
            hasPort = !rest.IsEmpty;
            port = hasPort ? rest[1..] : default;
        }
        else
        {
            int colon = authority.IndexOf(':');
            ReadOnlySpan<char> name = colon < 0 ? authority : authority[..colon];

            // The host-name characters exclude '/', '?', '#', '@', '*', whitespace and non-ASCII,
            // so a path, query, fragment, user information or wildcard fails here.
            if (name.IsEmpty || name.ContainsAnyExcept(_hostCharacters))
            {
                return false;
            }

            host = name.ToString().ToLowerInvariant();
            hasPort = colon >= 0;
            port = hasPort ? authority[(colon + 1)..] : default;
        }

        string schemeText = scheme.ToString().ToLowerInvariant();
        string portSuffix = string.Empty;

        if (hasPort)
        {
            if (port.IsEmpty
                || port.Length > 5
                || port.ContainsAnyExceptInRange('0', '9')
                || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || number > ushort.MaxValue)
            {
                return false;
            }

            if (number != GetDefaultPort(schemeText))
            {
                portSuffix = ":" + number.ToString(CultureInfo.InvariantCulture);
            }
        }

        serialized = string.Concat(schemeText, "://", host, portSuffix);
        return true;
    }

    // The URL standard's default ports, which an origin's serialization omits.
    private static int GetDefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        _ => -1,
    };
}

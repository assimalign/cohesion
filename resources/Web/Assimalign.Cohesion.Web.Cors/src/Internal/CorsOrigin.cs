using System;
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Assimalign.Cohesion.Web.Cors.Internal;

/// <summary>
/// Parses origins. A configured origin is validated and normalized to the serialized form a browser
/// sends in the <c>Origin</c> header (the HTML "serialization of an origin":
/// <c>scheme://host[:port]</c>, scheme and host lowercase, default port omitted). A request origin is
/// checked to be in that form already.
/// </summary>
/// <remarks>
/// Normalization covers only how an origin's own parts are written: letter case, a default port,
/// leading zeros in a port, and the spelling of an IPv6 address. Anything that is not part of an
/// origin is rejected rather than stripped: a path (including a trailing <c>/</c>), a query, a
/// fragment, user information, or a wildcard. A pasted URL therefore fails at configuration with a
/// message saying what to remove, instead of producing an origin that never matches.
/// </remarks>
internal static class CorsOrigin
{
    /// <summary>The serialization of an opaque origin (Fetch <c>origin-or-null</c>).</summary>
    internal const string Opaque = "null";

    // Host-name characters accepted in a configured origin: letters, digits, '-', '.', '_'. Stricter than
    // the URL parser, which also accepts a few sub-delimiters no real origin uses.
    private static readonly SearchValues<char> _hostCharacters =
        SearchValues.Create("-._0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // RFC 3986 §3.1: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    private static readonly SearchValues<char> _schemeCharacters =
        SearchValues.Create("+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Validates a configured origin and returns its serialized form.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not an origin.</exception>
    internal static string Normalize(string origin, string parameterName)
    {
        if (TryNormalize(origin, out string serialized, out string? error))
        {
            return serialized;
        }

        throw new ArgumentException(error, parameterName);
    }

    /// <summary>
    /// Gets whether <paramref name="value"/> is exactly a serialized origin, as a browser sends it, or
    /// the opaque origin <c>null</c>.
    /// </summary>
    internal static bool IsSerialized(string value)
        => value == Opaque
            || (TryNormalize(value, out string serialized, out _) && string.Equals(serialized, value, StringComparison.Ordinal));

    /// <summary>
    /// Parses <paramref name="value"/> as an origin and returns its serialized form, or the reason it is
    /// not an origin.
    /// </summary>
    internal static bool TryNormalize(string value, out string serialized, out string? error)
    {
        serialized = string.Empty;

        if (string.IsNullOrEmpty(value))
        {
            error = "An origin must not be empty.";
            return false;
        }

        if (value == "*")
        {
            error = "'*' is not an origin. Call AllowAnyOrigin() to allow every origin.";
            return false;
        }

        if (value.Equals(Opaque, StringComparison.OrdinalIgnoreCase))
        {
            error = "The 'null' origin cannot be allowed by value: sandboxed documents, local files and " +
                "cross-origin redirects all send it, so allowing it would allow all of them. Call " +
                "SetIsOriginAllowed to accept it deliberately.";
            return false;
        }

        foreach (char character in value)
        {
            if (character <= ' ' || character >= '\u007F')
            {
                error = $"'{value}' is not an origin: it contains whitespace, a control character or a non-ASCII " +
                    "character. Write an internationalized host in its ASCII (punycode) form, which is what the " +
                    "browser sends.";
                return false;
            }
        }

        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd <= 0)
        {
            error = $"'{value}' is not an origin: expected scheme://host[:port], for example https://app.example.";
            return false;
        }

        ReadOnlySpan<char> scheme = value.AsSpan(0, schemeEnd);

        if (!char.IsAsciiLetter(scheme[0]) || scheme.ContainsAnyExcept(_schemeCharacters))
        {
            error = $"'{value}' is not an origin: '{scheme}' is not a URL scheme.";
            return false;
        }

        ReadOnlySpan<char> authority = value.AsSpan(schemeEnd + 3);
        int pathStart = authority.IndexOfAny('/', '?', '#');

        if (pathStart >= 0)
        {
            ReadOnlySpan<char> extra = authority[pathStart..];
            error = extra is "/"
                ? $"'{value}' is not an origin: an origin has no path. Remove the trailing '/'."
                : $"'{value}' is not an origin: an origin has no path, query or fragment. Remove '{extra}'.";
            return false;
        }

        if (authority.Contains('@'))
        {
            error = $"'{value}' is not an origin: an origin has no user information.";
            return false;
        }

        if (authority.Contains('*'))
        {
            error = $"'{value}' is not an origin: wildcards are not supported. Call SetIsOriginAllowed to match " +
                "a pattern, or AllowAnyOrigin() to allow every origin.";
            return false;
        }

        if (!TryParseHost(value, authority, out string host, out bool hasPort, out ReadOnlySpan<char> port, out error))
        {
            return false;
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
                error = $"'{value}' is not an origin: '{port}' is not a port number.";
                return false;
            }

            if (number != GetDefaultPort(schemeText))
            {
                portSuffix = ":" + number.ToString(CultureInfo.InvariantCulture);
            }
        }

        serialized = string.Concat(schemeText, "://", host, portSuffix);
        error = null;
        return true;
    }

    private static bool TryParseHost(
        string value,
        ReadOnlySpan<char> authority,
        out string host,
        out bool hasPort,
        out ReadOnlySpan<char> port,
        out string? error)
    {
        host = string.Empty;
        hasPort = false;
        port = default;

        if (authority.IsEmpty)
        {
            error = $"'{value}' is not an origin: it has no host.";
            return false;
        }

        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            ReadOnlySpan<char> literal = close < 0 ? authority[1..] : authority[1..close];

            if (close < 0
                || literal.Contains('%')
                || !IPAddress.TryParse(literal, out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = $"'{value}' is not an origin: its host is not an IPv6 address.";
                return false;
            }

            ReadOnlySpan<char> rest = authority[(close + 1)..];

            if (!rest.IsEmpty && rest[0] != ':')
            {
                error = $"'{value}' is not an origin: expected ':port' after the IPv6 address.";
                return false;
            }

            host = "[" + SerializeIPv6(address) + "]";
            hasPort = !rest.IsEmpty;
            port = hasPort ? rest[1..] : default;
            error = null;
            return true;
        }

        int colon = authority.IndexOf(':');
        ReadOnlySpan<char> name = colon < 0 ? authority : authority[..colon];

        if (name.IsEmpty)
        {
            error = $"'{value}' is not an origin: it has no host.";
            return false;
        }

        if (name.ContainsAnyExcept(_hostCharacters))
        {
            error = $"'{value}' is not an origin: '{name}' is not a host name.";
            return false;
        }

        host = name.ToString().ToLowerInvariant();
        hasPort = colon >= 0;
        port = hasPort ? authority[(colon + 1)..] : default;
        error = null;
        return true;
    }

    // The URL standard's default ports; an origin's serialization omits them.
    private static int GetDefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        "ftp" => 21,
        _ => -1,
    };

    // The URL standard's IPv6 serializer: lowercase hexadecimal pieces without leading zeros, and the
    // first longest run of two or more zero pieces compressed to "::".
    private static string SerializeIPv6(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);

        Span<ushort> pieces = stackalloc ushort[8];
        for (int i = 0; i < pieces.Length; i++)
        {
            pieces[i] = (ushort)((bytes[2 * i] << 8) | bytes[(2 * i) + 1]);
        }

        int compress = -1;
        int longest = 1;
        for (int i = 0; i < pieces.Length;)
        {
            if (pieces[i] != 0)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < pieces.Length && pieces[i] == 0)
            {
                i++;
            }

            if (i - start > longest)
            {
                compress = start;
                longest = i - start;
            }
        }

        StringBuilder builder = new(39);
        bool ignoreZero = false;

        for (int i = 0; i < pieces.Length; i++)
        {
            if (ignoreZero && pieces[i] == 0)
            {
                continue;
            }

            ignoreZero = false;

            if (compress == i)
            {
                builder.Append(i == 0 ? "::" : ":");
                ignoreZero = true;
                continue;
            }

            builder.Append(pieces[i].ToString("x", CultureInfo.InvariantCulture));

            if (i != pieces.Length - 1)
            {
                builder.Append(':');
            }
        }

        return builder.ToString();
    }
}

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

    // Numeric parsers accept trailing NUL characters, so validate the serialized alphabet first.
    private static readonly SearchValues<char> _portCharacters = SearchValues.Create("0123456789");
    private static readonly SearchValues<char> _ipv6PieceCharacters = SearchValues.Create("0123456789abcdef");

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
    {
        if (value == Opaque)
        {
            return true;
        }

        // Request validation must not construct the normalized copy used for configuration. In
        // particular, a successful origin predicate is part of allocation-free preflight evaluation.
        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> scheme = value.AsSpan(0, schemeEnd);
        if (scheme[0] is < 'a' or > 'z' || scheme.ContainsAnyExcept(_schemeCharacters)
            || ContainsUppercase(scheme))
        {
            return false;
        }

        ReadOnlySpan<char> authority = value.AsSpan(schemeEnd + 3);
        if (authority.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<char> suffix;
        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0 || !IsSerializedIPv6(authority[1..close]))
            {
                return false;
            }

            suffix = authority[(close + 1)..];
        }
        else
        {
            int colon = authority.IndexOf(':');
            ReadOnlySpan<char> host = colon < 0 ? authority : authority[..colon];
            if (host.IsEmpty || host.ContainsAnyExcept(_hostCharacters) || ContainsUppercase(host))
            {
                return false;
            }

            suffix = colon < 0 ? default : authority[colon..];
        }

        if (suffix.IsEmpty)
        {
            return true;
        }

        if (suffix[0] != ':')
        {
            return false;
        }

        ReadOnlySpan<char> port = suffix[1..];
        return !port.IsEmpty && port.Length <= 5 && (port.Length == 1 || port[0] != '0')
            && !port.ContainsAnyExcept(_portCharacters)
            && int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number <= ushort.MaxValue && number != GetDefaultPort(scheme);
    }

    // A serialized IPv6 host has eight hexadecimal pieces, with the first longest run of at least
    // two zero pieces compressed. IPv4 suffixes, uppercase and leading zeros are not serialized form.
    private static bool IsSerializedIPv6(ReadOnlySpan<char> host)
    {
        Span<ushort> pieces = stackalloc ushort[8];
        int compression = host.IndexOf("::", StringComparison.Ordinal);
        int count = 0;
        int leftCount = 0;
        if (compression >= 0)
        {
            if (!TryReadIPv6Pieces(host[..compression], pieces, ref count))
            {
                return false;
            }

            leftCount = count;
            if (!TryReadIPv6Pieces(host[(compression + 2)..], pieces, ref count) || count > 6)
            {
                return false;
            }

            int omitted = 8 - count;
            pieces[leftCount..count].CopyTo(pieces[(leftCount + omitted)..]);
            pieces.Slice(leftCount, omitted).Clear();
        }
        else if (!TryReadIPv6Pieces(host, pieces, ref count) || count != 8)
        {
            return false;
        }

        int longestStart = -1;
        int longestLength = 1;
        for (int index = 0; index < pieces.Length;)
        {
            if (pieces[index] != 0)
            {
                index++;
                continue;
            }

            int start = index;
            while (index < pieces.Length && pieces[index] == 0)
            {
                index++;
            }

            if (index - start > longestLength)
            {
                longestStart = start;
                longestLength = index - start;
            }
        }

        return compression < 0 ? longestStart < 0 : longestStart == leftCount && longestLength == 8 - count;
    }

    private static bool TryReadIPv6Pieces(ReadOnlySpan<char> text, Span<ushort> pieces, ref int count)
    {
        if (text.IsEmpty)
        {
            return true;
        }

        foreach (Range range in text.Split(':'))
        {
            ReadOnlySpan<char> piece = text[range];
            if (count == pieces.Length || piece.IsEmpty || piece.Length > 4
                || (piece.Length > 1 && piece[0] == '0')
                || piece.ContainsAnyExcept(_ipv6PieceCharacters)
                || !ushort.TryParse(piece, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort number))
            {
                return false;
            }

            pieces[count++] = number;
        }

        return true;
    }

    private static bool ContainsUppercase(ReadOnlySpan<char> text)
    {
        // Explicit character comparisons keep this check allocation-free in Debug builds too.
        foreach (char character in text)
        {
            if (character is >= 'A' and <= 'Z')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <paramref name="value"/> as an origin and returns its serialized form, or the reason it is
    /// not an origin.
    /// </summary>
    internal static bool TryNormalize(string value, out string serialized, out string? error)
    {
        serialized = string.Empty;

        if (string.IsNullOrEmpty(value))
        {
            error = ErrorMessages.OriginRequired;
            return false;
        }

        if (value == "*")
        {
            error = ErrorMessages.OriginWildcardOptionRequired;
            return false;
        }

        if (value.Equals(Opaque, StringComparison.OrdinalIgnoreCase))
        {
            error = ErrorMessages.OpaqueOriginPredicateRequired;
            return false;
        }

        foreach (char character in value)
        {
            if (character <= ' ' || character >= '\u007F')
            {
                error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginCharacterInvalid, value);
                return false;
            }
        }

        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd <= 0)
        {
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginSyntaxInvalid, value);
            return false;
        }

        ReadOnlySpan<char> scheme = value.AsSpan(0, schemeEnd);

        if (!char.IsAsciiLetter(scheme[0]) || scheme.ContainsAnyExcept(_schemeCharacters))
        {
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginSchemeInvalid, value, scheme.ToString());
            return false;
        }

        ReadOnlySpan<char> authority = value.AsSpan(schemeEnd + 3);
        int pathStart = authority.IndexOfAny('/', '?', '#');

        if (pathStart >= 0)
        {
            ReadOnlySpan<char> extra = authority[pathStart..];
            error = extra is "/"
                ? string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginTrailingSlashNotAllowed, value)
                : string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginPathQueryOrFragmentNotAllowed, value, extra.ToString());
            return false;
        }

        if (authority.Contains('@'))
        {
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginUserInformationNotAllowed, value);
            return false;
        }

        if (authority.Contains('*'))
        {
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginWildcardNotSupported, value);
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
                error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginPortInvalid, value, port.ToString());
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
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginHostRequired, value);
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
                error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginIpv6AddressInvalid, value);
                return false;
            }

            ReadOnlySpan<char> rest = authority[(close + 1)..];

            if (!rest.IsEmpty && rest[0] != ':')
            {
                error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginIpv6PortSeparatorRequired, value);
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
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginHostRequired, value);
            return false;
        }

        if (name.ContainsAnyExcept(_hostCharacters))
        {
            error = string.Format(CultureInfo.CurrentCulture, ErrorMessages.OriginHostInvalid, value, name.ToString());
            return false;
        }

        host = name.ToString().ToLowerInvariant();
        hasPort = colon >= 0;
        port = hasPort ? authority[(colon + 1)..] : default;
        error = null;
        return true;
    }

    // The URL standard's default ports; an origin's serialization omits them.
    private static int GetDefaultPort(ReadOnlySpan<char> scheme) => scheme switch
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

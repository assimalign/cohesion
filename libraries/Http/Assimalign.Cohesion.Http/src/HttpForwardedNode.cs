using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed RFC 7239 &#167; 6 <c>node</c> identifier — the value carried by the <c>for</c> and
/// <c>by</c> parameters of a <see cref="HttpForwardedElement"/>. A node is a <c>nodename</c> with
/// an optional <c>node-port</c>: <c>nodename [ ":" node-port ]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>nodename</c> is one of an IPv4 literal, a bracketed IPv6 literal (<c>[2001:db8::1]</c>),
/// the sentinel <c>unknown</c>, or an obfuscated identifier (RFC 7239 &#167; 6.3, an <c>_</c>-prefixed
/// token such as <c>_gazonk</c>). The <c>node-port</c> is either a numeric port or an obfuscated
/// port (RFC 7239 &#167; 6.4). A proxy that wants to hide its address publishes an obfuscated node.
/// </para>
/// <para>
/// This type is deliberately reused for the entries of the de-facto <c>X-Forwarded-For</c> header,
/// which — unlike the strict RFC 7239 grammar — routinely writes IPv6 addresses <em>without</em>
/// brackets (<c>2001:db8::1</c>). Parsing therefore also accepts a bare IPv6 literal; when a bare
/// IPv6 literal is present it is treated as the whole nodename with no port, because there is no
/// unambiguous way to separate a trailing <c>:port</c> from the address's own colons (which is
/// precisely why RFC 7239 mandates brackets). <see cref="Name"/> preserves the exact spelling that
/// was parsed so <see cref="ToString"/> round-trips the wire form.
/// </para>
/// <para>
/// Recognizing an IPv4/IPv6 literal delegates to <see cref="IPAddress.TryParse(ReadOnlySpan{char}, out IPAddress)"/>,
/// so the address forms this type accepts are exactly those the BCL parser accepts. This type never
/// throws on hostile input: <see cref="TryParse(ReadOnlySpan{char}, out HttpForwardedNode)"/> returns
/// <see langword="false"/> for anything that is not a well-formed node.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpForwardedNode : IEquatable<HttpForwardedNode>
{
    private const string UnknownName = "unknown";

    private readonly string? name;
    private readonly string? port;
    private readonly IPAddress? address;

    private HttpForwardedNode(string name, string? port, IPAddress? address)
    {
        this.name = name;
        this.port = port;
        this.address = address;
    }

    /// <summary>The RFC 7239 <c>unknown</c> node identifier.</summary>
    public static HttpForwardedNode Unknown { get; } = new(UnknownName, null, null);

    /// <summary>
    /// Gets the raw <c>nodename</c> exactly as parsed — an IPv4 literal, a bracketed
    /// (<c>[2001:db8::1]</c>) or bare (<c>2001:db8::1</c>) IPv6 literal, <c>unknown</c>, or an
    /// obfuscated identifier. Empty when this is the default instance.
    /// </summary>
    public string Name => name ?? string.Empty;

    /// <summary>
    /// Gets the raw <c>node-port</c> (a numeric or obfuscated port) exactly as parsed, or
    /// <see langword="null"/> when the node carries no port.
    /// </summary>
    public string? Port => port;

    /// <summary>
    /// Gets the IP address of the node when <see cref="Name"/> is an IPv4 or IPv6 literal (brackets
    /// stripped), or <see langword="null"/> for an <c>unknown</c> or obfuscated node.
    /// </summary>
    public IPAddress? Address => address;

    /// <summary>
    /// Gets the numeric port when <see cref="Port"/> is a decimal number, or <see langword="null"/>
    /// when the node has no port or an obfuscated port.
    /// </summary>
    public int? PortNumber
        => port is not null && IsNumericPort(port.AsSpan()) ? int.Parse(port) : null;

    /// <summary>Gets a value indicating whether the node is the RFC 7239 <c>unknown</c> sentinel.</summary>
    public bool IsUnknown => name is not null && string.Equals(name, UnknownName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the nodename is an RFC 7239 &#167; 6.3 obfuscated identifier
    /// (begins with <c>_</c>).
    /// </summary>
    public bool IsObfuscatedName => name is { Length: > 0 } && name[0] == '_';

    /// <summary>
    /// Gets a value indicating whether the port is an RFC 7239 &#167; 6.4 obfuscated port
    /// (begins with <c>_</c>).
    /// </summary>
    public bool HasObfuscatedPort => port is { Length: > 0 } && port[0] == '_';

    /// <summary>Gets a value indicating whether this is the default (unparsed) instance.</summary>
    public bool IsEmpty => name is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Creates a node from an <see cref="IPAddress"/> and optional port, emitting the canonical
    /// RFC 7239 spelling (an IPv6 address is bracketed).
    /// </summary>
    /// <param name="address">The node address.</param>
    /// <param name="port">The optional numeric port.</param>
    /// <returns>A node whose <see cref="Address"/> is <paramref name="address"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is negative.</exception>
    public static HttpForwardedNode FromIPAddress(IPAddress address, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        string name = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return new HttpForwardedNode(name, port?.ToString(), address);
    }

    /// <summary>
    /// Parses <paramref name="value"/> as an RFC 7239 &#167; 6 node identifier.
    /// </summary>
    /// <param name="value">The node text (e.g. <c>192.0.2.60:4711</c>, <c>[2001:db8::1]</c>, <c>_hidden</c>).</param>
    /// <returns>The parsed node.</returns>
    /// <exception cref="HttpException">The value is not a well-formed node.</exception>
    public static HttpForwardedNode Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpForwardedNode result))
        {
            throw new HttpInvalidForwardedException($"The value is not a valid forwarded node: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an RFC 7239 &#167; 6 node identifier.
    /// </summary>
    /// <param name="value">The node text, or <see langword="null"/>.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed node.</param>
    /// <returns><see langword="true"/> when the value is a well-formed node.</returns>
    public static bool TryParse(string? value, out HttpForwardedNode result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an RFC 7239 &#167; 6 node identifier. Never
    /// throws; malformed input yields <see langword="false"/> and a default result.
    /// </summary>
    /// <param name="value">The node text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed node.</param>
    /// <returns><see langword="true"/> when the value is a well-formed node.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpForwardedNode result)
    {
        result = default;

        value = HttpFieldSyntax.TrimOws(value);
        if (value.IsEmpty)
        {
            return false;
        }

        // Bracketed IPv6 literal: "[" IPv6address "]" [ ":" node-port ].
        if (value[0] == '[')
        {
            int close = value.IndexOf(']');
            if (close < 2)
            {
                return false;
            }

            ReadOnlySpan<char> inner = value[1..close];
            if (!IPAddress.TryParse(inner, out IPAddress? ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            ReadOnlySpan<char> rest = value[(close + 1)..];
            string? bracketPort = null;
            if (!rest.IsEmpty)
            {
                if (rest[0] != ':' || !TryValidatePort(rest[1..], out bracketPort))
                {
                    return false;
                }
            }

            result = new HttpForwardedNode($"[{inner.ToString()}]", bracketPort, ipv6);
            return true;
        }

        // Bare IPv6 literal (X-Forwarded-For de-facto form): the whole value is the address, and a
        // trailing port cannot be disambiguated from the address's colons, so none is recognized.
        if (value.IndexOf(':') >= 0
            && IPAddress.TryParse(value, out IPAddress? bare)
            && bare.AddressFamily == AddressFamily.InterNetworkV6)
        {
            result = new HttpForwardedNode(value.ToString(), null, bare);
            return true;
        }

        // nodename [ ":" node-port ] — nodename is IPv4 / "unknown" / obfnode (none contain a colon).
        ReadOnlySpan<char> nameSpan = value;
        string? nodePort = null;
        int colon = value.IndexOf(':');
        if (colon >= 0)
        {
            nameSpan = value[..colon];
            if (!TryValidatePort(value[(colon + 1)..], out nodePort))
            {
                return false;
            }
        }

        if (!TryClassifyName(nameSpan, out IPAddress? nameAddress))
        {
            return false;
        }

        result = new HttpForwardedNode(nameSpan.ToString(), nodePort, nameAddress);
        return true;
    }

    private static bool TryClassifyName(ReadOnlySpan<char> nameSpan, out IPAddress? nameAddress)
    {
        nameAddress = null;
        if (nameSpan.IsEmpty)
        {
            return false;
        }

        if (nameSpan.Equals(UnknownName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (nameSpan[0] == '_')
        {
            return IsObfuscatedToken(nameSpan);
        }

        // An IPv4 literal is the only remaining valid nodename form (colon-free, non-obfuscated).
        if (IPAddress.TryParse(nameSpan, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            nameAddress = parsed;
            return true;
        }

        return false;
    }

    private static bool TryValidatePort(ReadOnlySpan<char> portSpan, out string? port)
    {
        port = null;
        if (portSpan.IsEmpty)
        {
            return false;
        }

        if (portSpan[0] == '_')
        {
            if (!IsObfuscatedToken(portSpan))
            {
                return false;
            }
        }
        else if (!IsNumericPort(portSpan))
        {
            return false;
        }

        port = portSpan.ToString();
        return true;
    }

    // port = 1*5DIGIT (RFC 7239 §6.4).
    private static bool IsNumericPort(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > 5)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    // obfnode / obfport = "_" 1*( ALPHA / DIGIT / "." / "_" / "-" ) (RFC 7239 §6.3, §6.4).
    private static bool IsObfuscatedToken(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != '_')
        {
            return false;
        }
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Returns the node in its RFC 7239 wire form (e.g. <c>[2001:db8::1]:4711</c>).</summary>
    /// <returns>The wire form, or an empty string for the default instance.</returns>
    public override string ToString()
        => IsEmpty ? string.Empty : port is null ? name! : string.Concat(name, ":", port);

    /// <inheritdoc />
    public bool Equals(HttpForwardedNode other)
        => string.Equals(name, other.name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(port, other.port, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpForwardedNode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(name),
            port is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(port));

    /// <summary>Determines whether two nodes are equal.</summary>
    public static bool operator ==(HttpForwardedNode left, HttpForwardedNode right) => left.Equals(right);

    /// <summary>Determines whether two nodes are not equal.</summary>
    public static bool operator !=(HttpForwardedNode left, HttpForwardedNode right) => !left.Equals(right);
}

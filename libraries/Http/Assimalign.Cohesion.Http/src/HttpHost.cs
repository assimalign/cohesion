using System;
using System.Diagnostics;
using System.Globalization;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP host value: the effective authority of a request as resolved by the
/// transport (the HTTP/1.1 <c>Host</c> header or request-target authority, or the HTTP/2 /
/// HTTP/3 <c>:authority</c> pseudo-header), carried as the raw <c>host[:port]</c> text.
/// </summary>
/// <remarks>
/// <para>
/// The raw <see cref="Value"/> is preserved exactly as it arrived on the wire; equality and
/// hashing compare the raw text case-insensitively. The component members
/// (<see cref="TryGetComponents"/>, <see cref="Host"/>, <see cref="Port"/>) split the value
/// into its normalized host and port parts on demand: the host component of a bracketed IPv6
/// literal is exposed <em>without</em> its brackets (so <c>[::1]</c> and <c>::1</c> yield the
/// same component), and an unbracketed value containing multiple colons is treated as an IPv6
/// literal without a port. These normalization rules deliberately mirror the Web routing
/// host-constraint semantics so that host <em>selection</em> (routing) and host
/// <em>validation</em> (allowlist filtering, see <see cref="HttpHostMatcher"/>) agree on what
/// a given wire value means.
/// </para>
/// </remarks>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHost : IEquatable<HttpHost>
{
    /// <summary>
    /// Gets an empty host value.
    /// </summary>
    public static HttpHost Empty { get; } = new(string.Empty);

    /// <summary>
    /// Initializes a new host value.
    /// </summary>
    /// <param name="value">The raw host value.</param>
    public HttpHost(string? value)
    {
        Value = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the raw host value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets a value indicating whether the raw value is empty (no host was present on the
    /// request and no authority could be resolved).
    /// </summary>
    public bool IsEmpty => Value is null || Value.Length == 0;

    /// <summary>
    /// Gets the normalized host component: the value without its port, with the brackets of an
    /// IPv6 literal removed and surrounding whitespace trimmed. When the value is not a
    /// well-formed <c>host[:port]</c> (see <see cref="TryGetComponents"/>), the raw
    /// <see cref="Value"/> is returned unchanged.
    /// </summary>
    public string Host
    {
        get
        {
            string value = Value ?? string.Empty;

            if (!TryGetComponents(out ReadOnlySpan<char> host, out _))
            {
                return value;
            }

            return host.Length == value.Length ? value : host.ToString();
        }
    }

    /// <summary>
    /// Gets the explicit port carried by the value, or <see langword="null"/> when the value
    /// carries no port or is not a well-formed <c>host[:port]</c>. An omitted port is not
    /// substituted with a scheme default — the component reflects only what was sent.
    /// </summary>
    public int? Port
    {
        get
        {
            return TryGetComponents(out _, out int? port) ? port : null;
        }
    }

    /// <summary>
    /// Splits the value into its host and port components without allocating.
    /// </summary>
    /// <param name="host">
    /// The host component: surrounding whitespace trimmed, the brackets of a bracketed IPv6
    /// literal removed. Empty when the value is empty; undefined when the method returns
    /// <see langword="false"/>.
    /// </param>
    /// <param name="port">
    /// The explicit port (1–65535), or <see langword="null"/> when the value carries none.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value is structurally a <c>host[:port]</c>:
    /// an empty value (no host present), a name or IPv4 literal with an optional decimal port,
    /// a bracketed IPv6 literal (<c>[::1]</c>, <c>[::1]:8080</c>), or an unbracketed IPv6
    /// literal (multiple colons, which therefore cannot carry a port). Returns
    /// <see langword="false"/> for an unterminated or empty bracket form, trailing junk after a
    /// closing bracket, a trailing colon with no port digits, or a port that is not a decimal
    /// integer in 1–65535.
    /// </returns>
    /// <remarks>
    /// The split is structural, not semantic: host characters are not validated against the
    /// URI <c>reg-name</c> grammar and IPv6 literal contents are not parsed as addresses.
    /// Consumers that compare hosts (routing constraints, allowlist matchers) operate on the
    /// normalized components and compare case-insensitively.
    /// </remarks>
    public bool TryGetComponents(out ReadOnlySpan<char> host, out int? port)
    {
        port = null;

        ReadOnlySpan<char> value = Value.AsSpan().Trim();
        host = value;

        if (value.IsEmpty)
        {
            return true;
        }

        if (value[0] == '[')
        {
            // Bracketed IPv6 literal: "[::1]" or "[::1]:8080". The exposed host component is
            // the literal without its brackets.
            int close = value.IndexOf(']');
            if (close <= 1)
            {
                host = default;
                return false;
            }

            host = value[1..close];
            ReadOnlySpan<char> rest = value[(close + 1)..];

            if (rest.IsEmpty)
            {
                return true;
            }

            if (rest[0] != ':' || rest.Length == 1 || !TryParsePort(rest[1..], out int bracketedPort))
            {
                host = default;
                return false;
            }

            port = bracketedPort;
            return true;
        }

        int first = value.IndexOf(':');
        if (first < 0)
        {
            return true;
        }

        if (value.LastIndexOf(':') != first)
        {
            // Multiple colons without brackets: an unbracketed IPv6 literal, tolerated
            // bracket-insensitively; such a value cannot carry a port component.
            return true;
        }

        if (first == value.Length - 1 || !TryParsePort(value[(first + 1)..], out int parsed))
        {
            // Trailing "host:" with no digits, or a port that is not a decimal 1-65535.
            host = default;
            return false;
        }

        host = value[..first];
        port = parsed;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(HttpHost other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is HttpHost other && Equals(other);
    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static implicit operator HttpHost(string value) => new(value);
    public static implicit operator string(HttpHost host) => host.Value;

    private static bool TryParsePort(ReadOnlySpan<char> value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}

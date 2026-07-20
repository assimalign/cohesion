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

        // Reuse the shared structural split (also used by the Web routing host constraint, so
        // selection and validation cannot drift), then layer this primitive's stricter port
        // rule on top: a component split that surfaces a "port" must have validated it, so a
        // present-but-invalid port makes the whole value malformed here — where the routing
        // constraint instead tolerates junk port text on a port-unconstrained route.
        if (!TrySplitHostPort(Value.AsSpan().Trim(), out host, out ReadOnlySpan<char> portText, out bool hasPort))
        {
            host = default;
            return false;
        }

        if (hasPort)
        {
            if (!TryParsePort(portText, out int parsed))
            {
                host = default;
                return false;
            }

            port = parsed;
        }

        return true;
    }

    /// <summary>
    /// Splits a <c>host[:port]</c> value into its host and (unparsed) port-text components using
    /// the structural rules shared by the Web routing host constraint: a bracketed IPv6 literal
    /// exposes its host without brackets, an unbracketed value with multiple colons is an IPv6
    /// literal that carries no port, and a single-colon value splits at that colon.
    /// </summary>
    /// <param name="value">The value to split. Callers trim before calling; this method does not.</param>
    /// <param name="host">The host component (IPv6 brackets removed); undefined when the method returns <see langword="false"/>.</param>
    /// <param name="port">The raw port text (digits only when structurally present); empty when <paramref name="hasPort"/> is <see langword="false"/>.</param>
    /// <param name="hasPort">
    /// <see langword="true"/> when a port component is syntactically present. The port text is
    /// <em>not</em> range-validated here — a present-but-out-of-range or non-numeric port still
    /// reports <see langword="true"/> so callers can choose to reject it (<see cref="TryGetComponents"/>)
    /// or tolerate it. Validate with <see cref="TryParsePort"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value is structurally <c>host[:port]</c>; <see langword="false"/>
    /// for an unterminated or empty bracket form, trailing junk after a closing bracket, or a
    /// trailing colon with no port digits.
    /// </returns>
    internal static bool TrySplitHostPort(ReadOnlySpan<char> value, out ReadOnlySpan<char> host, out ReadOnlySpan<char> port, out bool hasPort)
    {
        host = value;
        port = default;
        hasPort = false;

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
                return false;
            }

            host = value[1..close];
            ReadOnlySpan<char> rest = value[(close + 1)..];

            if (rest.IsEmpty)
            {
                return true;
            }

            if (rest[0] != ':' || rest.Length == 1)
            {
                // Junk after the closing bracket, or a trailing "]:" with no port digits.
                return false;
            }

            port = rest[1..];
            hasPort = true;
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

        if (first == value.Length - 1)
        {
            // Trailing "host:" with no port digits.
            return false;
        }

        host = value[..first];
        port = value[(first + 1)..];
        hasPort = true;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(HttpHost other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is HttpHost other && Equals(other);
    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static implicit operator HttpHost(string value) => new(value);
    public static implicit operator string(HttpHost host) => host.Value;

    /// <summary>
    /// Parses a port component: a decimal integer in the range 1–65535, with no sign,
    /// whitespace, or other <see cref="NumberStyles"/> leniency. Shared with the Web routing
    /// host constraint so both paths apply the identical port rule.
    /// </summary>
    /// <param name="value">The port text to parse.</param>
    /// <param name="port">The parsed port when the text is a valid 1–65535 decimal.</param>
    /// <returns><see langword="true"/> when the text is a valid port; otherwise <see langword="false"/>.</returns>
    internal static bool TryParsePort(ReadOnlySpan<char> value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}

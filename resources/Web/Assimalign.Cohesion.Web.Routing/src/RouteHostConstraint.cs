using System;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Exceptions;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Represents a single parsed host constraint declared by a route: an exact host
/// (<c>api.example.com</c>), a wildcard subdomain (<c>*.example.com</c>), or any host
/// (<c>*</c>), each optionally combined with an explicit port
/// (<c>api.example.com:8080</c>, <c>*:5000</c>, <c>[::1]:8080</c>).
/// </summary>
/// <remarks>
/// <para>
/// Host comparison is case-insensitive (RFC 9110 §4.2.3 / RFC 3986 §3.2.2). A wildcard
/// constraint requires at least one subdomain label: <c>*.example.com</c> matches
/// <c>api.example.com</c> and <c>a.b.example.com</c> but not <c>example.com</c> itself.
/// A port constraint compares against the port written explicitly in the request's
/// <c>Host</c> value; a request whose host omits the port (an implied default port) does
/// not satisfy a port-constrained route.
/// </para>
/// <para>
/// IPv6 literals are written in bracketed form (<c>[::1]</c>, <c>[::1]:8080</c>) and are
/// stored and compared without the brackets, so <c>[::1]</c> and <c>::1</c> describe the
/// same constraint. A <see langword="default"/> instance matches nothing; use
/// <see cref="Parse"/> or <see cref="TryParse"/> to create constraints.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct RouteHostConstraint : IEquatable<RouteHostConstraint>
{
    private readonly string? _host;
    private readonly int? _port;

    private RouteHostConstraint(string host, int? port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>
    /// Gets the host portion of the constraint: an exact host name, a <c>*.suffix</c>
    /// wildcard, <c>*</c> for any host, or an IPv6 literal without brackets.
    /// </summary>
    public string Host => _host ?? string.Empty;

    /// <summary>
    /// Gets the explicit port the request host must carry, or <see langword="null"/>
    /// when the constraint accepts any port.
    /// </summary>
    public int? Port => _port;

    /// <summary>
    /// Gets a value indicating whether the constraint accepts any host
    /// (its <see cref="Host"/> is <c>*</c>), constraining only the port if one is set.
    /// </summary>
    public bool MatchesAnyHost => _host is "*";

    /// <summary>
    /// Gets a value indicating whether the constraint is a wildcard-subdomain pattern
    /// (its <see cref="Host"/> starts with <c>*.</c>).
    /// </summary>
    public bool IsSubdomainWildcard => _host is not null && _host.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>
    /// Parses a host-constraint pattern.
    /// </summary>
    /// <param name="pattern">
    /// The pattern to parse: <c>host</c>, <c>*.host</c>, or <c>*</c>, optionally followed by
    /// <c>:port</c>. IPv6 literals use bracketed form (<c>[::1]</c>).
    /// </param>
    /// <returns>The parsed <see cref="RouteHostConstraint"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="RoutePatternException"><paramref name="pattern"/> is not a well-formed host constraint.</exception>
    public static RouteHostConstraint Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (!TryParse(pattern, out RouteHostConstraint constraint))
        {
            throw new RoutePatternException(
                pattern,
                $"The value is not a valid route host constraint: '{pattern}'. Expected 'host', '*.host', or '*', optionally followed by ':port' (1-65535).");
        }

        return constraint;
    }

    /// <summary>
    /// Attempts to parse a host-constraint pattern.
    /// </summary>
    /// <param name="pattern">
    /// The pattern to parse: <c>host</c>, <c>*.host</c>, or <c>*</c>, optionally followed by
    /// <c>:port</c>. IPv6 literals use bracketed form (<c>[::1]</c>).
    /// </param>
    /// <param name="constraint">The parsed constraint when the pattern is well formed.</param>
    /// <returns><see langword="true"/> when the pattern is a well-formed host constraint; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? pattern, out RouteHostConstraint constraint)
    {
        constraint = default;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = pattern.AsSpan().Trim();

        if (!TrySplitHostAndPort(trimmed, out ReadOnlySpan<char> host, out ReadOnlySpan<char> portText, out bool hasPort) ||
            host.IsEmpty)
        {
            return false;
        }

        int? port = null;
        if (hasPort)
        {
            if (!TryParsePort(portText, out int parsed))
            {
                return false;
            }

            port = parsed;
        }

        if (!IsValidHostPattern(host))
        {
            return false;
        }

        constraint = new RouteHostConstraint(host.ToString(), port);
        return true;
    }

    /// <summary>
    /// Determines whether the supplied request host satisfies the constraint.
    /// </summary>
    /// <param name="host">The request host to test, as produced by <c>IHttpRequest.Host</c>.</param>
    /// <returns><see langword="true"/> when the host satisfies the constraint; otherwise <see langword="false"/>.</returns>
    public bool IsMatch(HttpHost host)
    {
        string? constraintHost = _host;

        if (constraintHost is null)
        {
            // A default-initialized constraint matches nothing.
            return false;
        }

        ReadOnlySpan<char> value = host.Value.AsSpan().Trim();

        if (!TrySplitHostAndPort(value, out ReadOnlySpan<char> name, out ReadOnlySpan<char> portText, out bool hasPort))
        {
            // A malformed request host never satisfies a host-constrained route.
            return false;
        }

        if (_port is int requiredPort)
        {
            if (!hasPort || !TryParsePort(portText, out int requestPort) || requestPort != requiredPort)
            {
                return false;
            }
        }

        if (constraintHost is "*")
        {
            return true;
        }

        if (IsSubdomainWildcard)
        {
            // "*.example.com" -> suffix ".example.com"; require at least one character of
            // subdomain label ahead of the suffix so the apex host does not match.
            ReadOnlySpan<char> suffix = constraintHost.AsSpan(1);
            return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return name.Equals(constraintHost.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool Equals(RouteHostConstraint other) =>
        _port == other._port && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RouteHostConstraint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Host), _port);

    /// <summary>
    /// Returns the canonical text of the constraint, bracketing IPv6 literals and appending
    /// the port when one is set (e.g. <c>*.example.com</c>, <c>[::1]:8080</c>).
    /// </summary>
    /// <returns>The canonical constraint text.</returns>
    public override string ToString()
    {
        string host = Host;

        if (host.Contains(':'))
        {
            host = $"[{host}]";
        }

        return _port is int port
            ? string.Create(CultureInfo.InvariantCulture, $"{host}:{port}")
            : host;
    }

    private static bool IsValidHostPattern(ReadOnlySpan<char> host)
    {
        if (host is "*")
        {
            return true;
        }

        if (host.StartsWith("*.", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> suffix = host[2..];
            return !suffix.IsEmpty && suffix.IndexOf('*') < 0;
        }

        return host.IndexOf('*') < 0;
    }

    private static bool TrySplitHostAndPort(ReadOnlySpan<char> value, out ReadOnlySpan<char> host, out ReadOnlySpan<char> port, out bool hasPort)
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
            // Bracketed IPv6 literal: "[::1]" or "[::1]:8080". The stored/compared host is
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
            // Multiple colons without brackets: an unbracketed IPv6 literal, which cannot
            // carry a port component.
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

    private static bool TryParsePort(ReadOnlySpan<char> value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}

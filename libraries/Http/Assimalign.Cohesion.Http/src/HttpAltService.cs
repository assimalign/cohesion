using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single RFC 7838 §3 <c>Alt-Svc</c> alternative service (<em>alt-value</em>): an
/// ALPN <see cref="ProtocolId"/> bound to an <em>alt-authority</em>
/// (<see cref="Host"/> plus <see cref="Port"/>), with the optional caching parameters
/// <see cref="MaxAgeSeconds"/> (<c>ma</c>) and <see cref="Persist"/> (<c>persist</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the typed projection of one member of the <c>Alt-Svc</c> response header field
/// (<see cref="HttpHeaderKey.AltSvc"/>). The header advertises alternative endpoints — most
/// commonly the HTTP/3 (QUIC) listener — so a client that started on HTTP/1.1 or HTTP/2 can
/// discover them (RFC 9114 §3.1). The value object formats and parses a single alt-value;
/// <see cref="FormatHeader(IReadOnlyList{HttpAltService})"/> and
/// <see cref="TryParseHeader(ReadOnlySpan{char}, out IReadOnlyList{HttpAltService}, out bool)"/>
/// handle the comma-separated list form and the special <c>clear</c> token.
/// </para>
/// <para>
/// The alt-authority is serialized as a quoted-string of the form <c>[uri-host] ":" port</c>
/// (RFC 7838 §3): the host is omitted (empty) to advertise an alternative on the request's own
/// host, and supplied to point at a different host. Parsing is tolerant of the optional host and
/// of unrecognized parameters (which are ignored), but a missing or non-numeric port is rejected.
/// </para>
/// <para>
/// The type is span-based and allocation-light on the format path, carries no reflection, and is
/// therefore AOT-safe.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct HttpAltService : IEquatable<HttpAltService>
{
    /// <summary>
    /// The case-sensitive <c>clear</c> token (RFC 7838 §3) that, when it is the entire
    /// <c>Alt-Svc</c> field value, tells a client to invalidate every cached alternative service
    /// for the origin.
    /// </summary>
    public const string ClearToken = "clear";

    /// <summary>The ALPN protocol identifier of the HTTP/3 alternative (<c>h3</c>).</summary>
    public const string Http3ProtocolId = "h3";

    private const string MaxAgeParameter = "ma";
    private const string PersistParameter = "persist";

    /// <summary>
    /// Initializes a new alternative service.
    /// </summary>
    /// <param name="protocolId">The ALPN protocol identifier (an RFC 7230 token, for example <c>h3</c>).</param>
    /// <param name="host">
    /// The alt-authority host, or <see langword="null"/>/empty to advertise the alternative on the
    /// request's own host.
    /// </param>
    /// <param name="port">The alt-authority port, in the range 0..65535.</param>
    /// <param name="maxAgeSeconds">
    /// The freshness lifetime in seconds (the <c>ma</c> parameter), or <see langword="null"/> to
    /// omit it (RFC 7838 §3.1 default of 24 hours applies at the client).
    /// </param>
    /// <param name="persist">
    /// Whether the alternative should persist across network changes (the <c>persist=1</c>
    /// parameter, RFC 7838 §3.1).
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="protocolId"/> is empty or contains a non-token character.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="port"/> is outside 0..65535, or <paramref name="maxAgeSeconds"/>
    /// is negative.
    /// </exception>
    public HttpAltService(string protocolId, string? host, int port, long? maxAgeSeconds = null, bool persist = false)
    {
        if (string.IsNullOrEmpty(protocolId) || !IsToken(protocolId))
        {
            throw new ArgumentException("The protocol identifier must be a non-empty RFC 7230 token.", nameof(protocolId));
        }

        if (port < 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be within 0..65535.");
        }

        if (maxAgeSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgeSeconds), maxAgeSeconds, "The max-age must be non-negative.");
        }

        ProtocolId = protocolId;
        Host = string.IsNullOrEmpty(host) ? null : host;
        Port = port;
        MaxAgeSeconds = maxAgeSeconds;
        Persist = persist;
    }

    /// <summary>Gets the ALPN protocol identifier of the alternative (for example <c>h3</c>).</summary>
    public string ProtocolId { get; }

    /// <summary>
    /// Gets the alt-authority host, or <see langword="null"/> when the alternative is advertised on
    /// the request's own host.
    /// </summary>
    public string? Host { get; }

    /// <summary>Gets the alt-authority port.</summary>
    public int Port { get; }

    /// <summary>
    /// Gets the freshness lifetime in seconds (the <c>ma</c> parameter), or <see langword="null"/>
    /// when it is absent.
    /// </summary>
    public long? MaxAgeSeconds { get; }

    /// <summary>
    /// Gets a value indicating whether the alternative persists across network changes (the
    /// <c>persist=1</c> parameter).
    /// </summary>
    public bool Persist { get; }

    /// <summary>
    /// Creates an HTTP/3 (<c>h3</c>) alternative service.
    /// </summary>
    /// <param name="host">
    /// The alt-authority host, or <see langword="null"/>/empty to advertise HTTP/3 on the request's
    /// own host (the common case).
    /// </param>
    /// <param name="port">The UDP port the QUIC listener is bound to.</param>
    /// <param name="maxAgeSeconds">The freshness lifetime in seconds, or <see langword="null"/> to omit <c>ma</c>.</param>
    /// <param name="persist">Whether to emit <c>persist=1</c>.</param>
    /// <returns>The HTTP/3 alternative service.</returns>
    public static HttpAltService Http3(string? host, int port, long? maxAgeSeconds = null, bool persist = false)
        => new(Http3ProtocolId, host, port, maxAgeSeconds, persist);

    /// <summary>
    /// Serializes this alternative to its RFC 7838 §3 alt-value form, for example
    /// <c>h3=":443"; ma=86400</c>.
    /// </summary>
    /// <returns>The canonical alt-value text.</returns>
    public string Format()
    {
        StringBuilder builder = new();
        builder.Append(ProtocolId);
        builder.Append("=\"");
        AppendAltAuthority(builder);
        builder.Append('"');

        if (MaxAgeSeconds is long maxAge)
        {
            builder.Append("; ").Append(MaxAgeParameter).Append('=').Append(maxAge.ToString(CultureInfo.InvariantCulture));
        }

        if (Persist)
        {
            builder.Append("; ").Append(PersistParameter).Append("=1");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Serializes a list of alternatives to a single RFC 7838 §3 <c>Alt-Svc</c> field value,
    /// joining the alt-values with <c>", "</c>.
    /// </summary>
    /// <param name="services">The alternatives to serialize.</param>
    /// <returns>
    /// The comma-separated field value, or the empty string when <paramref name="services"/> is
    /// empty (an empty <c>Alt-Svc</c> value is not emitted).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static string FormatHeader(IReadOnlyList<HttpAltService> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        for (int i = 0; i < services.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(services[i].Format());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a single RFC 7838 §3 alt-value (for example <c>h3=":443"; ma=86400</c>).
    /// </summary>
    /// <param name="input">The alt-value text.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the parsed alternative service.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="input"/> is a well-formed alt-value with a valid
    /// alt-authority port; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpAltService result)
    {
        result = default;

        ReadOnlySpan<char> remaining = TrimOptionalWhitespace(input);
        if (remaining.IsEmpty)
        {
            return false;
        }

        // protocol-id "=" alt-authority
        int equalsIndex = remaining.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> protocolId = TrimOptionalWhitespace(remaining.Slice(0, equalsIndex));
        if (protocolId.IsEmpty || !IsToken(protocolId))
        {
            return false;
        }

        remaining = TrimOptionalWhitespace(remaining.Slice(equalsIndex + 1));

        // alt-authority is a quoted-string.
        if (!TryReadQuotedString(ref remaining, out string altAuthority))
        {
            return false;
        }

        if (!TryParseAltAuthority(altAuthority, out string? host, out int port))
        {
            return false;
        }

        long? maxAge = null;
        bool persist = false;

        // *( OWS ";" OWS parameter )
        remaining = TrimOptionalWhitespace(remaining);
        while (!remaining.IsEmpty)
        {
            if (remaining[0] != ';')
            {
                return false;
            }

            remaining = TrimOptionalWhitespace(remaining.Slice(1));

            if (!TryReadParameter(ref remaining, out ReadOnlySpan<char> name, out string value))
            {
                return false;
            }

            if (name.Equals(MaxAgeParameter, StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedMaxAge))
                {
                    return false;
                }

                maxAge = parsedMaxAge;
            }
            else if (name.Equals(PersistParameter, StringComparison.OrdinalIgnoreCase))
            {
                // RFC 7838 §3.1 — persist=1 is the only defined value; any other value clears it.
                persist = value == "1";
            }

            // Unrecognized parameters are ignored (RFC 7838 §3).
            remaining = TrimOptionalWhitespace(remaining);
        }

        result = new HttpAltService(protocolId.ToString(), host, port, maxAge, persist);
        return true;
    }

    /// <summary>
    /// Parses a complete RFC 7838 §3 <c>Alt-Svc</c> field value: either the case-sensitive
    /// <c>clear</c> token, or a comma-separated list of alt-values.
    /// </summary>
    /// <param name="value">The field value to parse.</param>
    /// <param name="services">
    /// When this method returns <see langword="true"/>, the parsed alternatives (empty when
    /// <paramref name="isClear"/> is <see langword="true"/>).
    /// </param>
    /// <param name="isClear">
    /// When this method returns <see langword="true"/>, whether the value was the <c>clear</c> token.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value is <c>clear</c> or contains at least one well-formed
    /// alt-value; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParseHeader(ReadOnlySpan<char> value, out IReadOnlyList<HttpAltService> services, out bool isClear)
    {
        services = Array.Empty<HttpAltService>();
        isClear = false;

        ReadOnlySpan<char> trimmed = TrimOptionalWhitespace(value);
        if (trimmed.IsEmpty)
        {
            return false;
        }

        // RFC 7838 §3 — "clear" is case-sensitive and is the entire field value.
        if (trimmed.Equals(ClearToken, StringComparison.Ordinal))
        {
            isClear = true;
            return true;
        }

        List<HttpAltService> parsed = new();
        foreach (Range segment in SplitTopLevelCommas(trimmed))
        {
            ReadOnlySpan<char> element = TrimOptionalWhitespace(trimmed[segment]);
            if (element.IsEmpty)
            {
                continue;
            }

            if (!TryParse(element, out HttpAltService service))
            {
                return false;
            }

            parsed.Add(service);
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        services = parsed;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(HttpAltService other)
        => string.Equals(ProtocolId, other.ProtocolId, StringComparison.Ordinal)
        && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && MaxAgeSeconds == other.MaxAgeSeconds
        && Persist == other.Persist;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpAltService other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            ProtocolId,
            Host is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Host),
            Port,
            MaxAgeSeconds,
            Persist);

    /// <inheritdoc />
    public override string ToString() => Format();

    /// <summary>Determines whether two alternatives are equal.</summary>
    /// <param name="left">The first alternative.</param>
    /// <param name="right">The second alternative.</param>
    /// <returns><see langword="true"/> if the alternatives are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(HttpAltService left, HttpAltService right) => left.Equals(right);

    /// <summary>Determines whether two alternatives are unequal.</summary>
    /// <param name="left">The first alternative.</param>
    /// <param name="right">The second alternative.</param>
    /// <returns><see langword="true"/> if the alternatives are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(HttpAltService left, HttpAltService right) => !left.Equals(right);

    private void AppendAltAuthority(StringBuilder builder)
    {
        if (Host is not null)
        {
            // Defensively escape the quoted-string metacharacters, though a uri-host never
            // legitimately contains them (RFC 7838 §3 / RFC 7230 §3.2.6).
            foreach (char c in Host)
            {
                if (c is '"' or '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(c);
            }
        }

        builder.Append(':');
        builder.Append(Port.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryParseAltAuthority(string altAuthority, out string? host, out int port)
    {
        host = null;
        port = 0;

        // alt-authority = [ uri-host ] ":" port. Split at the LAST colon so an IPv6 literal
        // host (e.g. "[::1]") keeps its own colons.
        int colonIndex = altAuthority.LastIndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> portSpan = altAuthority.AsSpan(colonIndex + 1);
        if (portSpan.IsEmpty
            || !int.TryParse(portSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort)
            || parsedPort < 0
            || parsedPort > 65535)
        {
            return false;
        }

        port = parsedPort;
        if (colonIndex > 0)
        {
            host = altAuthority.Substring(0, colonIndex);
        }

        return true;
    }

    private static bool TryReadQuotedString(ref ReadOnlySpan<char> input, out string value)
    {
        value = string.Empty;

        if (input.IsEmpty || input[0] != '"')
        {
            return false;
        }

        StringBuilder builder = new();
        int index = 1;
        while (index < input.Length)
        {
            char c = input[index];
            if (c == '\\')
            {
                // quoted-pair: the backslash escapes the next character.
                if (index + 1 >= input.Length)
                {
                    return false;
                }

                builder.Append(input[index + 1]);
                index += 2;
                continue;
            }

            if (c == '"')
            {
                value = builder.ToString();
                input = input.Slice(index + 1);
                return true;
            }

            builder.Append(c);
            index++;
        }

        // Unterminated quoted-string.
        return false;
    }

    private static bool TryReadParameter(ref ReadOnlySpan<char> input, out ReadOnlySpan<char> name, out string value)
    {
        name = default;
        value = string.Empty;

        int nameLength = 0;
        while (nameLength < input.Length && IsTokenChar(input[nameLength]))
        {
            nameLength++;
        }

        if (nameLength == 0)
        {
            return false;
        }

        name = input.Slice(0, nameLength);
        ReadOnlySpan<char> afterName = TrimOptionalWhitespace(input.Slice(nameLength));
        if (afterName.IsEmpty || afterName[0] != '=')
        {
            return false;
        }

        ReadOnlySpan<char> afterEquals = TrimOptionalWhitespace(afterName.Slice(1));
        if (afterEquals.IsEmpty)
        {
            return false;
        }

        if (afterEquals[0] == '"')
        {
            if (!TryReadQuotedString(ref afterEquals, out value))
            {
                return false;
            }

            input = afterEquals;
            return true;
        }

        int valueLength = 0;
        while (valueLength < afterEquals.Length && IsTokenChar(afterEquals[valueLength]))
        {
            valueLength++;
        }

        if (valueLength == 0)
        {
            return false;
        }

        value = afterEquals.Slice(0, valueLength).ToString();
        input = afterEquals.Slice(valueLength);
        return true;
    }

    private static IEnumerable<Range> SplitTopLevelCommas(ReadOnlySpan<char> input)
    {
        // Commas inside a quoted-string are part of the alt-authority, not list separators, so the
        // split must skip over quoted regions. Materialized up front because a span cannot be
        // captured by the iterator.
        List<Range> ranges = new();
        int start = 0;
        bool inQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\\' && inQuotes)
            {
                // Skip the escaped character.
                i++;
            }
            else if (c == ',' && !inQuotes)
            {
                ranges.Add(new Range(start, i));
                start = i + 1;
            }
        }

        ranges.Add(new Range(start, input.Length));
        return ranges;
    }

    private static ReadOnlySpan<char> TrimOptionalWhitespace(ReadOnlySpan<char> input)
    {
        // RFC 7230 OWS = *( SP / HTAB ).
        int start = 0;
        while (start < input.Length && (input[start] == ' ' || input[start] == '\t'))
        {
            start++;
        }

        int end = input.Length;
        while (end > start && (input[end - 1] == ' ' || input[end - 1] == '\t'))
        {
            end--;
        }

        return input.Slice(start, end - start);
    }

    private static bool IsToken(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!IsTokenChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenChar(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*'
            or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}

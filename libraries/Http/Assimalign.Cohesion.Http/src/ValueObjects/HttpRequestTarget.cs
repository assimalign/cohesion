using System;
using System.Diagnostics;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed HTTP/1.1 request-target (RFC 9112 &#167; 3.2). Captures the form
/// (<see cref="HttpRequestTargetForm.Origin"/>,
/// <see cref="HttpRequestTargetForm.Absolute"/>,
/// <see cref="HttpRequestTargetForm.Authority"/>,
/// <see cref="HttpRequestTargetForm.Asterisk"/>) plus the components meaningful to that
/// form (scheme, host, path, query).
/// </summary>
/// <remarks>
/// <para>
/// Method/form pairings are enforced by <see cref="TryParse(System.ReadOnlySpan{char},
/// HttpMethod, out HttpRequestTarget, out string)"/>:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="HttpMethod.Connect"/> requires
///   <see cref="HttpRequestTargetForm.Authority"/> &#8211; no other method may use it.</description></item>
///   <item><description><see cref="HttpRequestTargetForm.Asterisk"/> is reserved for
///   <see cref="HttpMethod.Options"/> only.</description></item>
///   <item><description><see cref="HttpRequestTargetForm.Origin"/> and
///   <see cref="HttpRequestTargetForm.Absolute"/> are valid for every other method.</description></item>
/// </list>
/// </remarks>
[DebuggerDisplay("{Form,nq}: {RawValue,nq}")]
public readonly struct HttpRequestTarget : IEquatable<HttpRequestTarget>
{
    /// <summary>
    /// The canonical asterisk target used by <c>OPTIONS *</c> requests.
    /// </summary>
    public static HttpRequestTarget Asterisk { get; } = new(
        HttpRequestTargetForm.Asterisk,
        HttpScheme.None,
        HttpHost.Empty,
        new HttpPath("*"),
        new HttpQuery(string.Empty),
        "*");

    private HttpRequestTarget(
        HttpRequestTargetForm form,
        HttpScheme scheme,
        HttpHost host,
        HttpPath path,
        HttpQuery query,
        string rawValue)
    {
        Form = form;
        Scheme = scheme;
        Host = host;
        Path = path;
        Query = query;
        RawValue = rawValue;
    }

    /// <summary>The parsed form of this request-target.</summary>
    public HttpRequestTargetForm Form { get; }

    /// <summary>
    /// Scheme parsed from an absolute-form target. <see cref="HttpScheme.None"/> for the
    /// other three forms (origin / authority / asterisk).
    /// </summary>
    public HttpScheme Scheme { get; }

    /// <summary>
    /// Host parsed from an absolute-form or authority-form target.
    /// <see cref="HttpHost.Empty"/> for origin-form and asterisk-form &#8211; consumers
    /// derive the host from the <c>Host</c> header instead.
    /// </summary>
    public HttpHost Host { get; }

    /// <summary>
    /// Path component of the target. For origin-form and absolute-form this is the
    /// absolute path. For authority-form this is <see cref="HttpPath.Root"/>. For
    /// asterisk-form this is the literal asterisk path.
    /// </summary>
    public HttpPath Path { get; }

    /// <summary>
    /// Query string. Empty for authority-form and asterisk-form.
    /// </summary>
    public HttpQuery Query { get; }

    /// <summary>
    /// The original raw target string as it appeared on the wire. Useful for logging,
    /// proxying, and round-tripping the request line unchanged.
    /// </summary>
    public string RawValue { get; }

    /// <summary>
    /// Parses <paramref name="raw"/> as a request-target appropriate for
    /// <paramref name="method"/>. Returns <see langword="false"/> when the target is
    /// malformed or violates the method/form pairing.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<char> raw,
        HttpMethod method,
        out HttpRequestTarget result)
        => TryParse(raw, method, out result, out _);

    /// <summary>
    /// Parses <paramref name="raw"/> as a request-target appropriate for
    /// <paramref name="method"/>. On failure, <paramref name="error"/> carries a
    /// human-readable explanation.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<char> raw,
        HttpMethod method,
        out HttpRequestTarget result,
        out string? error)
    {
        result = default;
        error = null;

        if (raw.IsEmpty)
        {
            error = "Request-target is empty.";
            return false;
        }
        if (ContainsControlOrSpace(raw, out int badIndex))
        {
            error = $"Request-target contains an invalid character at position {badIndex}.";
            return false;
        }

        // RFC 9112 §3.2.4 — the literal '*' is asterisk-form. Reserved for OPTIONS.
        if (raw.Length == 1 && raw[0] == '*')
        {
            if (!IsOptions(method))
            {
                error = $"Asterisk-form request-target is reserved for OPTIONS; got method '{method.Value}'.";
                return false;
            }
            result = Asterisk;
            return true;
        }

        // RFC 9112 §3.2.3 — CONNECT MUST use authority-form (host:port).
        bool isConnect = string.Equals(method.Value, "CONNECT", StringComparison.Ordinal);
        if (isConnect)
        {
            return TryParseAuthorityForm(raw, out result, out error);
        }

        // Distinguish origin-form (starts with '/') from absolute-form (contains '://').
        if (raw[0] == '/')
        {
            return TryParseOriginForm(raw, out result, out error);
        }

        if (LooksLikeAbsoluteForm(raw))
        {
            return TryParseAbsoluteForm(raw, out result, out error);
        }

        // Anything else (e.g. authority-form with a non-CONNECT method) is malformed.
        error = $"Request-target '{raw.ToString()}' does not match any RFC 9112 §3.2 form for method '{method.Value}'.";
        return false;
    }

    /// <summary>
    /// Parses <paramref name="raw"/> as a request-target appropriate for
    /// <paramref name="method"/>. Throws <see cref="HttpException"/> when the target is
    /// malformed; use <see cref="TryParse(System.ReadOnlySpan{char}, HttpMethod, out HttpRequestTarget)"/>
    /// when the caller wants the bool-out shape instead.
    /// </summary>
    /// <exception cref="HttpException">The target is malformed or violates the method/form
    /// pairing.</exception>
    public static HttpRequestTarget Parse(ReadOnlySpan<char> raw, HttpMethod method)
    {
        if (!TryParse(raw, method, out HttpRequestTarget result, out string? error))
        {
            throw new HttpInvalidRequestTargetException(error ?? "Malformed request-target.");
        }
        return result;
    }

    private static bool TryParseOriginForm(
        ReadOnlySpan<char> raw,
        out HttpRequestTarget result,
        out string? error)
    {
        result = default;
        error = null;

        int queryIndex = raw.IndexOf('?');
        ReadOnlySpan<char> pathSpan = queryIndex < 0 ? raw : raw[..queryIndex];
        ReadOnlySpan<char> querySpan = queryIndex < 0 ? ReadOnlySpan<char>.Empty : raw[(queryIndex + 1)..];

        if (pathSpan.IsEmpty || pathSpan[0] != '/')
        {
            error = "origin-form request-target must begin with '/'.";
            return false;
        }

        HttpPath path;
        try
        {
            path = new HttpPath(pathSpan.ToString());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        result = new HttpRequestTarget(
            HttpRequestTargetForm.Origin,
            HttpScheme.None,
            HttpHost.Empty,
            path,
            new HttpQuery(querySpan.ToString()),
            raw.ToString());
        return true;
    }

    private static bool TryParseAbsoluteForm(
        ReadOnlySpan<char> raw,
        out HttpRequestTarget result,
        out string? error)
    {
        result = default;
        error = null;

        if (!Uri.TryCreate(raw.ToString(), UriKind.Absolute, out Uri? uri))
        {
            error = "absolute-form request-target is not a valid absolute URI.";
            return false;
        }

        HttpScheme scheme = uri.Scheme switch
        {
            "http" => HttpScheme.Http,
            "https" => HttpScheme.Https,
            _ => HttpScheme.None,
        };
        if (scheme == HttpScheme.None)
        {
            error = $"absolute-form request-target uses unsupported scheme '{uri.Scheme}'.";
            return false;
        }
        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "absolute-form request-target is missing the authority component.";
            return false;
        }

        // Host as it appeared on the wire — preserve the [port] suffix per RFC 9112 §3.2.2.
        string hostWithPort = uri.IsDefaultPort
            ? uri.Host
            : $"{uri.Host}:{uri.Port}";

        HttpPath path;
        try
        {
            path = new HttpPath(string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        string queryString = uri.Query.Length > 0 && uri.Query[0] == '?' ? uri.Query[1..] : uri.Query;
        result = new HttpRequestTarget(
            HttpRequestTargetForm.Absolute,
            scheme,
            new HttpHost(hostWithPort),
            path,
            new HttpQuery(queryString),
            raw.ToString());
        return true;
    }

    private static bool TryParseAuthorityForm(
        ReadOnlySpan<char> raw,
        out HttpRequestTarget result,
        out string? error)
    {
        result = default;
        error = null;

        // CONNECT targets are uri-host ":" port. The host may be a registered name, an
        // IPv4 literal, or a bracketed IPv6 literal.
        int hostEnd;
        if (raw[0] == '[')
        {
            int closeBracket = raw.IndexOf(']');
            if (closeBracket < 0)
            {
                error = "authority-form request-target has an unmatched '['.";
                return false;
            }
            hostEnd = closeBracket + 1;
        }
        else
        {
            hostEnd = raw.IndexOf(':');
            if (hostEnd < 0)
            {
                error = "authority-form request-target must include a port (host:port).";
                return false;
            }
        }

        if (hostEnd >= raw.Length || raw[hostEnd] != (raw[0] == '[' ? ']' : ':'))
        {
            // hostEnd == raw.Length when the bracket-IPv6 has no trailing port.
            if (raw[0] == '[' && hostEnd == raw.Length)
            {
                error = "authority-form request-target must include a port (host:port).";
                return false;
            }
        }

        int portStart = raw[0] == '[' ? hostEnd : hostEnd;
        // If the host was bracketed IPv6, hostEnd points past the closing bracket.
        // Expect a ':' immediately after.
        if (raw[0] == '[')
        {
            if (portStart >= raw.Length || raw[portStart] != ':')
            {
                error = "authority-form request-target with IPv6 host must include a port (e.g. [::1]:8080).";
                return false;
            }
        }

        // Now portStart points at the ':' that precedes the port.
        ReadOnlySpan<char> hostSpan = raw[..portStart];
        ReadOnlySpan<char> portSpan = raw[(portStart + 1)..];

        if (hostSpan.IsEmpty)
        {
            error = "authority-form request-target has an empty host.";
            return false;
        }
        if (portSpan.IsEmpty)
        {
            error = "authority-form request-target has an empty port.";
            return false;
        }
        foreach (char c in portSpan)
        {
            if (c < '0' || c > '9')
            {
                error = $"authority-form request-target port '{portSpan.ToString()}' contains non-digit characters.";
                return false;
            }
        }

        result = new HttpRequestTarget(
            HttpRequestTargetForm.Authority,
            HttpScheme.None,
            new HttpHost(raw.ToString()),
            HttpPath.Root,
            new HttpQuery(string.Empty),
            raw.ToString());
        return true;
    }

    private static bool LooksLikeAbsoluteForm(ReadOnlySpan<char> raw)
    {
        // RFC 3986 absolute-URI requires a scheme followed by ":" and authority-prefixed
        // with "//" — i.e. the substring "://" appears before any '/' or '?' in the target.
        int schemeSep = raw.IndexOf(':');
        if (schemeSep <= 0)
        {
            return false;
        }
        if (schemeSep + 2 >= raw.Length)
        {
            return false;
        }
        if (raw[schemeSep + 1] != '/' || raw[schemeSep + 2] != '/')
        {
            return false;
        }
        // Reject schemes that contain '/' or '?' (those would indicate origin / mixed form).
        for (int i = 0; i < schemeSep; i++)
        {
            char c = raw[i];
            if (c == '/' || c == '?')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsOptions(HttpMethod method)
        => string.Equals(method.Value, "OPTIONS", StringComparison.Ordinal);

    private static bool ContainsControlOrSpace(ReadOnlySpan<char> raw, out int index)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c <= 0x1F || c == 0x7F || c == ' ' || c == '\t')
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(HttpRequestTarget other)
        => Form == other.Form
        && Scheme == other.Scheme
        && Host.Equals(other.Host)
        && Path.Equals(other.Path)
        && string.Equals(Query.Value, other.Query.Value, StringComparison.Ordinal)
        && string.Equals(RawValue, other.RawValue, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpRequestTarget other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Form, Scheme, Host, Path, Query.Value, RawValue);

    /// <inheritdoc />
    public override string ToString() => RawValue;

    public static bool operator ==(HttpRequestTarget left, HttpRequestTarget right) => left.Equals(right);
    public static bool operator !=(HttpRequestTarget left, HttpRequestTarget right) => !left.Equals(right);
}

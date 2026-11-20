using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The field rules a proxy applies between its two connections: which fields belong to one
/// connection only and never travel on (RFC 9110 §7.6.1), and the token grammar the <c>Via</c>
/// pseudonym and <c>Connection</c> options use.
/// </summary>
internal static class HttpProxyFields
{
    // RFC 9110 §5.6.2 tchar.
    private static readonly SearchValues<char> _tokenCharacters = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // RFC 9110 §7.6.1 names Connection, Proxy-Connection, Keep-Alive, TE, Transfer-Encoding and
    // Upgrade. Proxy-Authorization is consumed by the proxy it authenticates to, HTTP2-Settings
    // belongs to an h2c upgrade of the client's connection, and Trailer announces request trailers,
    // which the forwarded request never carries. TE is removed here and re-added as "trailers" alone
    // where the client asked for it (gRPC requires it on the next hop).
    private static readonly FrozenSet<string> _requestFields = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Connection",
        "Keep-Alive",
        "Proxy-Connection",
        "TE",
        "Transfer-Encoding",
        "Upgrade",
        "Proxy-Authorization",
        "HTTP2-Settings",
        "Trailer");

    // The response side of the same rule. Proxy-Authenticate challenges the proxy, not the client.
    // Alt-Svc advertises the destination's own alternative services, which the client cannot reach
    // through this proxy. Trailer is handled separately because it survives where trailers do.
    private static readonly FrozenSet<string> _responseFields = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Connection",
        "Keep-Alive",
        "Proxy-Connection",
        "Transfer-Encoding",
        "Upgrade",
        "Proxy-Authenticate",
        "Alt-Svc");

    /// <summary>
    /// The fields a client's forwarded hop is described with, of both families.
    /// </summary>
    private static readonly FrozenSet<string> _forwardedFields = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "X-Forwarded-For",
        "X-Forwarded-Proto",
        "X-Forwarded-Host",
        "Forwarded");

    /// <summary>
    /// Returns whether <paramref name="value"/> is a non-empty HTTP token (RFC 9110 §5.6.2).
    /// </summary>
    public static bool IsToken(ReadOnlySpan<char> value)
    {
        return !value.IsEmpty && !value.ContainsAnyExcept(_tokenCharacters);
    }

    /// <summary>
    /// Returns whether a request field belongs to the client's connection only.
    /// </summary>
    public static bool IsRequestHopByHop(string name) => _requestFields.Contains(name);

    /// <summary>
    /// Returns whether a response field belongs to the destination's connection only.
    /// </summary>
    public static bool IsResponseHopByHop(string name) => _responseFields.Contains(name);

    /// <summary>
    /// Returns whether a field describes a forwarded hop.
    /// </summary>
    public static bool IsForwardedField(string name) => _forwardedFields.Contains(name);

    /// <summary>
    /// Returns whether a forwarded field belongs to <paramref name="format"/>'s family.
    /// </summary>
    public static bool IsInFamily(string name, HttpForwardedHeaderFormat format)
    {
        bool isStandard = string.Equals(name, "Forwarded", StringComparison.OrdinalIgnoreCase);
        return format == HttpForwardedHeaderFormat.Forwarded ? isStandard : !isStandard;
    }

    /// <summary>
    /// Collects the field names a <c>Connection</c> header lists, which are hop-by-hop for that
    /// message too (RFC 9110 §7.6.1). Returns <see langword="null"/> when it lists none.
    /// </summary>
    public static HashSet<string>? GetConnectionOptions(IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return null;
        }

        HashSet<string>? options = null;

        foreach (string? value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (Range range in value.AsSpan().Split(','))
            {
                ReadOnlySpan<char> option = value.AsSpan(range).Trim(" \t");
                if (IsToken(option))
                {
                    options ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    options.Add(option.ToString());
                }
            }
        }

        return options;
    }

    /// <summary>
    /// Returns whether a <c>TE</c> header asks for trailers, the one value a proxy forwards.
    /// </summary>
    public static bool AcceptsTrailers(HttpHeaderValue value)
    {
        foreach (string? item in value)
        {
            if (string.IsNullOrEmpty(item))
            {
                continue;
            }

            foreach (Range range in item.AsSpan().Split(','))
            {
                ReadOnlySpan<char> coding = item.AsSpan(range).Trim(" \t");
                int parameters = coding.IndexOf(';');
                if (parameters >= 0)
                {
                    coding = coding[..parameters].TrimEnd(" \t");
                }

                if (coding.Equals("trailers", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a <c>Via</c> header lists <paramref name="pseudonym"/> as a received-by entry.
    /// </summary>
    public static bool ViaContains(HttpHeaderValue value, string pseudonym)
    {
        foreach (string? item in value)
        {
            if (string.IsNullOrEmpty(item))
            {
                continue;
            }

            foreach (Range range in item.AsSpan().Split(','))
            {
                // received-protocol RWS received-by [ RWS comment ]
                ReadOnlySpan<char> entry = item.AsSpan(range).Trim(" \t");
                int space = entry.IndexOfAny(' ', '\t');
                if (space < 0)
                {
                    continue;
                }

                ReadOnlySpan<char> receivedBy = entry[(space + 1)..].TrimStart(" \t");
                int end = receivedBy.IndexOfAny(' ', '\t');
                if (end >= 0)
                {
                    receivedBy = receivedBy[..end];
                }

                if (receivedBy.Equals(pseudonym, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the <c>Via</c> received-protocol for the client's HTTP version (RFC 9110 §7.6.3: the
    /// protocol name is omitted for HTTP).
    /// </summary>
    public static string GetViaProtocol(HttpVersion version) => version switch
    {
        HttpVersion.Http20 => "2",
        HttpVersion.Http30 => "3",
        _ => "1.1",
    };

    /// <summary>
    /// Returns the <c>Via</c> received-protocol for a response the destination sent.
    /// </summary>
    public static string GetViaProtocol(Version version) => version.Major switch
    {
        2 => "2",
        3 => "3",
        1 when version.Minor == 0 => "1.0",
        _ => "1.1",
    };

    /// <summary>
    /// Returns the scheme name a forwarded hop reports, or <see langword="null"/> when it is unknown.
    /// </summary>
    public static string? GetSchemeName(HttpScheme scheme) => scheme switch
    {
        HttpScheme.Http => "http",
        HttpScheme.Https => "https",
        _ => null,
    };
}

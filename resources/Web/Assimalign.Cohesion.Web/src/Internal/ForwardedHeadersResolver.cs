using System;
using System.Collections.Generic;
using System.Net;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// Applies the forwarded-headers trust model to one exchange: walks the forwarded chain
/// rightmost-first (nearest hop first), accepting an entry only while the peer that
/// handed it over is trusted, and produces the exchange's <see cref="HttpForwardedFeature"/>.
/// Construction validates and snapshots a <see cref="ForwardedHeadersOptions"/>, so a
/// resolver is immutable and safe to share across concurrent exchanges.
/// </summary>
/// <remarks>
/// The walk semantics, in one place:
/// <list type="bullet">
/// <item><description>
/// Entry <c>r</c> (counting from the right) applies only when the peer that appended it
/// is trusted — the directly connected transport peer for <c>r = 0</c>, the address
/// adopted from entry <c>r - 1</c> after that. The first untrusted peer stops the walk;
/// entries beyond it are attacker-writable and ignored.
/// </description></item>
/// <item><description>
/// A hop whose honored values include anything malformed (an unparseable
/// <c>X-Forwarded-For</c> node, a <c>proto</c> that is not <c>http</c>/<c>https</c>, an
/// implausible <c>host</c>) stops the walk <em>before</em> any of that hop's values
/// apply. Hops already accepted stay applied — they were vouched for independently.
/// </description></item>
/// <item><description>
/// A hop whose <c>for</c> node carries no IP address (<c>unknown</c>, an obfuscated
/// identifier, or an absent <c>for</c>) applies its scheme/host values — the trusted
/// peer vouched for the whole entry — but the walk stops after it: with no address
/// there is nothing to check the next entry's issuer against.
/// </description></item>
/// <item><description>
/// A present-but-unusable RFC 7239 <c>Forwarded</c> header (its parser is strict and
/// all-or-nothing) resolves <em>nothing</em>, and in particular does not fall back to
/// the legacy family — a malformed header must never buy an attacker a different,
/// more favorable evaluation path.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class ForwardedHeadersResolver
{
    private readonly ForwardedHeaders _headers;
    private readonly int? _forwardLimit;
    private readonly IPAddress[] _knownProxies;
    private readonly IPNetwork[] _knownNetworks;
    private readonly bool _trustLocalTransports;

    /// <summary>
    /// Validates <paramref name="options"/> and snapshots it. The snapshot is deep for
    /// the trust inputs (proxy/network lists are copied, addresses normalized), so later
    /// mutation of the options object cannot change a composed pipeline.
    /// </summary>
    /// <param name="options">The trust-model options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ForwardedHeadersOptions.Headers"/> is <see cref="ForwardedHeaders.None"/>,
    /// or a trust list contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ForwardedHeadersOptions.ForwardLimit"/> is less than 1.
    /// </exception>
    public ForwardedHeadersResolver(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Headers == ForwardedHeaders.None)
        {
            throw new ArgumentException(
                $"{nameof(ForwardedHeadersOptions.Headers)} must select at least one forwarding header. " +
                "Header selection is part of the trust model — enable exactly the headers your trusted proxy manages.",
                nameof(options));
        }
        if (options.ForwardLimit is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ForwardLimit,
                $"{nameof(ForwardedHeadersOptions.ForwardLimit)} must be at least 1, or null for no limit.");
        }

        var proxies = new List<IPAddress>(options.KnownProxies.Count);
        foreach (IPAddress? proxy in options.KnownProxies)
        {
            if (proxy is null)
            {
                throw new ArgumentException(
                    $"{nameof(ForwardedHeadersOptions.KnownProxies)} contains a null entry.", nameof(options));
            }
            proxies.Add(Normalize(proxy));
        }

        _headers = options.Headers;
        _forwardLimit = options.ForwardLimit;
        _knownProxies = proxies.ToArray();
        _knownNetworks = [.. options.KnownNetworks];
        _trustLocalTransports = options.TrustLocalTransports;
    }

    /// <summary>
    /// Resolves the effective connection identity for <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The exchange to resolve.</param>
    /// <returns>The resolution outcome; never <see langword="null"/>.</returns>
    public HttpForwardedFeature Resolve(IHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Resolve(
            context.ConnectionInfo.RemoteEndPoint,
            context.Request.Headers,
            context.Request.Scheme,
            context.Request.Host);
    }

    /// <summary>
    /// Resolves the effective connection identity from raw exchange inputs.
    /// </summary>
    /// <param name="remoteEndPoint">The transport-level remote endpoint.</param>
    /// <param name="headers">The request headers (read-only use; never mutated).</param>
    /// <param name="scheme">The wire-level request scheme.</param>
    /// <param name="host">The wire-level request host.</param>
    /// <returns>The resolution outcome; never <see langword="null"/>.</returns>
    public HttpForwardedFeature Resolve(EndPoint? remoteEndPoint, IHttpHeaderCollection headers, HttpScheme scheme, HttpHost host)
    {
        ArgumentNullException.ThrowIfNull(headers);

        ResolutionState state = new()
        {
            Scheme = scheme,
            Host = host,
            Remote = remoteEndPoint,
            Anchor = remoteEndPoint,
        };

        if ((_headers & ForwardedHeaders.Forwarded) != 0
            && headers.TryGetValue(HttpHeaderKey.Forwarded, out HttpHeaderValue forwarded))
        {
            // RFC 7239 is present and honored — it is the exclusive source for this
            // exchange (families are never mixed; their entries cannot be correlated
            // hop-for-hop). Present-but-unusable resolves nothing and deliberately does
            // not fall through to the legacy family.
            if (HttpForwardedElementCollection.TryParse(forwarded, out HttpForwardedElementCollection elements))
            {
                WalkForwarded(elements, ref state);
            }
        }
        else
        {
            WalkXForwarded(headers, ref state);
        }

        return new HttpForwardedFeature(state.Scheme, state.Host, state.Remote, scheme, host, remoteEndPoint, state.Hops);
    }

    private void WalkForwarded(HttpForwardedElementCollection elements, ref ResolutionState state)
    {
        for (int depth = 0; depth < elements.Count; depth++)
        {
            if (_forwardLimit is int limit && state.Hops >= limit)
            {
                return;
            }
            if (!IsTrustedPeer(state.Anchor))
            {
                return;
            }

            HttpForwardedElement element = elements[elements.Count - 1 - depth];

            // Validate everything the hop asserts before applying any of it.
            HttpScheme entryScheme = default;
            string? proto = element.Proto;
            if (proto is not null && !TryMapScheme(proto, out entryScheme))
            {
                return;
            }
            string? hostValue = element.Host;
            if (hostValue is not null && !IsPlausibleHost(hostValue))
            {
                return;
            }

            IPAddress? entryAddress = null;
            int entryPort = 0;
            if (element.For is { } forNode && forNode.Address is { } address)
            {
                entryAddress = Normalize(address);
                entryPort = forNode.PortNumber ?? 0;
            }

            if (entryAddress is not null)
            {
                state.Remote = new IPEndPoint(entryAddress, entryPort);
            }
            if (proto is not null)
            {
                state.Scheme = entryScheme;
            }
            if (hostValue is not null)
            {
                state.Host = new HttpHost(hostValue);
            }
            state.Hops++;

            if (entryAddress is null)
            {
                // 'for' was absent, 'unknown', or obfuscated: the hop's values were
                // vouched for and applied, but there is no address to check the next
                // entry's issuer against — the walk cannot continue.
                return;
            }
            state.Anchor = new IPEndPoint(entryAddress, 0);
        }
    }

    private void WalkXForwarded(IHttpHeaderCollection headers, ref ResolutionState state)
    {
        HttpForwardedValues forValues = default;
        HttpForwardedValues protoValues = default;
        HttpForwardedValues hostValues = default;

        bool hasFor = (_headers & ForwardedHeaders.XForwardedFor) != 0
            && headers.TryGetValue(HttpHeaderKey.XForwardedFor, out HttpHeaderValue forHeader)
            && HttpForwardedValues.TryParse(forHeader, out forValues);
        bool hasProto = (_headers & ForwardedHeaders.XForwardedProto) != 0
            && headers.TryGetValue(HttpHeaderKey.XForwardedProto, out HttpHeaderValue protoHeader)
            && HttpForwardedValues.TryParse(protoHeader, out protoValues);
        bool hasHost = (_headers & ForwardedHeaders.XForwardedHost) != 0
            && headers.TryGetValue(HttpHeaderKey.XForwardedHost, out HttpHeaderValue hostHeader)
            && HttpForwardedValues.TryParse(hostHeader, out hostValues);

        // The X-Forwarded-For entries are the address chain that vouches for deeper
        // hops. Without it (absent or not honored), scheme/host values can only be
        // believed one hop deep — the entry appended by the directly connected peer.
        int chainLength = hasFor ? forValues.Count : (hasProto || hasHost ? 1 : 0);

        for (int depth = 0; depth < chainLength; depth++)
        {
            if (_forwardLimit is int limit && state.Hops >= limit)
            {
                return;
            }
            if (!IsTrustedPeer(state.Anchor))
            {
                return;
            }

            // Validate everything the hop asserts before applying any of it. The three
            // lists are correlated from the right: entry r of each header was appended
            // by the same proxy, r hops away.
            IPAddress? entryAddress = null;
            int entryPort = 0;
            if (hasFor)
            {
                if (!HttpForwardedNode.TryParse(forValues[forValues.Count - 1 - depth], out HttpForwardedNode node))
                {
                    return;
                }
                if (node.Address is { } address)
                {
                    entryAddress = Normalize(address);
                    entryPort = node.PortNumber ?? 0;
                }
            }

            HttpScheme entryScheme = default;
            string? proto = EntryAt(protoValues, hasProto, depth);
            if (proto is not null && !TryMapScheme(proto, out entryScheme))
            {
                return;
            }
            string? hostValue = EntryAt(hostValues, hasHost, depth);
            if (hostValue is not null && !IsPlausibleHost(hostValue))
            {
                return;
            }

            if (entryAddress is not null)
            {
                state.Remote = new IPEndPoint(entryAddress, entryPort);
            }
            if (proto is not null)
            {
                state.Scheme = entryScheme;
            }
            if (hostValue is not null)
            {
                state.Host = new HttpHost(hostValue);
            }
            state.Hops++;

            if (entryAddress is null)
            {
                // No address chain ('unknown'/obfuscated node, or X-Forwarded-For not in
                // play): nothing vouches for deeper entries.
                return;
            }
            state.Anchor = new IPEndPoint(entryAddress, 0);
        }
    }

    private static string? EntryAt(in HttpForwardedValues values, bool present, int depthFromRight)
        => present && depthFromRight < values.Count ? values[values.Count - 1 - depthFromRight] : null;

    private bool IsTrustedPeer(EndPoint? peer) => peer switch
    {
        null => false,
        IPEndPoint ip => IsTrustedAddress(Normalize(ip.Address)),
        // A DnsEndPoint never describes an accepted inbound connection and cannot be
        // classified as a machine-local transport — never trusted.
        DnsEndPoint => false,
        // Anything else is a non-IP transport endpoint (Unix domain socket, named pipe,
        // in-memory) — machine-local by construction in the Cohesion driver set.
        _ => _trustLocalTransports,
    };

    private bool IsTrustedAddress(IPAddress address)
    {
        foreach (IPAddress proxy in _knownProxies)
        {
            if (proxy.Equals(address))
            {
                return true;
            }
        }
        foreach (IPNetwork network in _knownNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }
        return false;
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryMapScheme(string value, out HttpScheme scheme)
    {
        if (value.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            scheme = HttpScheme.Https;
            return true;
        }
        if (value.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            scheme = HttpScheme.Http;
            return true;
        }
        scheme = HttpScheme.None;
        return false;
    }

    // A forwarded host is applied into downstream URL/origin decisions, so reject
    // anything that could smuggle a path, query, userinfo, or header structure. This is
    // a shape check, not an allowlist — restricting *which* hosts are acceptable is the
    // consumer's policy.
    private static bool IsPlausibleHost(string value)
    {
        if (value.Length is 0 or > 1024)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c <= ' ' || c == (char)0x7F)
            {
                return false;
            }
            if (c is '/' or '\\' or '?' or '#' or '@' or ',' or '"' or '<' or '>')
            {
                return false;
            }
        }
        return true;
    }

    private struct ResolutionState
    {
        public HttpScheme Scheme;
        public HttpHost Host;
        public EndPoint? Remote;
        public EndPoint? Anchor;
        public int Hops;
    }
}

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Per-resolver EDNS Cookie state (RFC 7873). Holds a single 8-octet client cookie and a
/// map of server cookies keyed by upstream IP, used to authenticate request/response pairs
/// against off-path spoofing.
/// </summary>
/// <remarks>
/// <para>
/// The client cookie is generated once per <see cref="DnsCookieStore"/> instance and reused
/// across all outgoing queries from that resolver. RFC 7873 &#167; 4 recommends a stable
/// client cookie per (client_ip, server_ip) pair; collapsing to a single cookie per resolver
/// is the standard simplification and matches what stub libraries do in practice.
/// </para>
/// <para>
/// Server cookies are 8&#8211;32 octets returned by RFC-7873-aware authorities. They are
/// cached by IP because the IP is what an off-path attacker would spoof &#8211; not the
/// server's DNS name. The cache is unbounded for now; real deployments roll server cookies
/// over slowly enough that a process-lifetime entry per upstream is fine.
/// </para>
/// </remarks>
internal sealed class DnsCookieStore
{
    private readonly byte[] _clientCookie;
    private readonly ConcurrentDictionary<IPAddress, byte[]> _serverCookies = new();

    /// <summary>
    /// Initializes a new store with an explicit client cookie. Tests use this to pin the
    /// cookie for deterministic assertions; production code calls
    /// <see cref="CreateRandom"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="clientCookie"/> is not exactly 8 octets.</exception>
    public DnsCookieStore(ReadOnlySpan<byte> clientCookie)
    {
        if (clientCookie.Length != 8)
        {
            throw new ArgumentException("Client cookie must be exactly 8 octets.", nameof(clientCookie));
        }
        _clientCookie = clientCookie.ToArray();
    }

    /// <summary>
    /// Creates a new store with a cryptographically random 8-octet client cookie.
    /// </summary>
    public static DnsCookieStore CreateRandom()
    {
        Span<byte> cookie = stackalloc byte[8];
        RandomNumberGenerator.Fill(cookie);
        return new DnsCookieStore(cookie);
    }

    /// <summary>The 8-octet client cookie this store sends with every outgoing query.</summary>
    public ReadOnlySpan<byte> ClientCookie => _clientCookie;

    /// <summary>
    /// Builds the EDNS Cookie option to attach to a query bound for <paramref name="serverIp"/>.
    /// Includes the cached server cookie when one is known so the upstream can authenticate
    /// the binding; otherwise sends a client-cookie-only option to elicit a server cookie.
    /// </summary>
    public DnsEdnsCookieOption BuildOption(IPAddress serverIp)
    {
        ArgumentNullException.ThrowIfNull(serverIp);
        return _serverCookies.TryGetValue(serverIp, out byte[]? server)
            ? new DnsEdnsCookieOption(_clientCookie, server)
            : new DnsEdnsCookieOption(_clientCookie);
    }

    /// <summary>
    /// Inspects <paramref name="response"/> for an EDNS Cookie option, validates the client
    /// cookie echo, and caches the server cookie under <paramref name="serverIp"/> when one
    /// is present.
    /// </summary>
    /// <returns><see langword="true"/> when the response carried a valid server cookie.</returns>
    public bool TryRecordServerCookie(IPAddress serverIp, DnsMessage response)
    {
        ArgumentNullException.ThrowIfNull(serverIp);
        ArgumentNullException.ThrowIfNull(response);

        DnsOptRecord? opt = response.Edns;
        if (opt is null)
        {
            return false;
        }

        foreach (DnsEdnsOption option in opt.Options)
        {
            if (option is not DnsEdnsCookieOption cookie)
            {
                continue;
            }
            // RFC 7873 §5.3: the client must verify the echoed client cookie matches the one
            // it sent. A mismatch indicates a spoofed response and is treated as a cookie
            // failure — we ignore the server cookie rather than caching it.
            if (!cookie.ClientCookie.SequenceEqual(_clientCookie))
            {
                return false;
            }
            if (cookie.HasServerCookie)
            {
                _serverCookies[serverIp] = cookie.ServerCookie.ToArray();
                return true;
            }
        }
        return false;
    }

    /// <summary>Forgets any cached server cookie for <paramref name="serverIp"/>.</summary>
    public void Forget(IPAddress serverIp)
    {
        ArgumentNullException.ThrowIfNull(serverIp);
        _serverCookies.TryRemove(serverIp, out _);
    }
}
